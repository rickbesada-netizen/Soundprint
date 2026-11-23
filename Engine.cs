using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Soundprint {
    sealed class Reference {
        public string Name;
        public double Tempo, Energy, Brightness;
        public double[] Chroma;
    }

    sealed class Profile {
        public int Tempo=110, Root=0;
        public bool Minor=true;
        public double Energy=.5, Brightness=.4;
        public int TempoCount;
    }

    sealed class SongSettings {
        public int Tempo, Bars, Root;
        public bool Minor;
        public double Energy, Brightness;
        public string Palette;
        public ulong Seed;
    }

    sealed class SeedRandom {
        ulong state;
        public SeedRandom(ulong seed) { state=seed; }
        public double Next() {
            unchecked {
                ulong z=(state+=0x9E3779B97F4A7C15UL);
                z=(z^(z>>30))*0xBF58476D1CE4E5B9UL;
                z=(z^(z>>27))*0x94D049BB133111EBUL;
                return ((z^(z>>31))>>11)*(1.0/9007199254740992.0);
            }
        }
        public int Pick(int count) { return (int)(Next()*count); }
    }

    static class Engine {
        public const int Rate=44100;
        public static readonly string[] Notes={"C","C#","D","Eb","E","F","F#","G","Ab","A","Bb","B"};
        public static readonly string[] Palettes={"Match references","Analog","Club","Cinematic","Lo-fi","Bass music"};
        static readonly int[][] Scales={new[]{0,2,4,5,7,9,11},new[]{0,2,3,5,7,8,10}};
        static readonly int[][] Progressions={new[]{0,5,3,4},new[]{0,3,1,4},new[]{0,4,5,3},new[]{0,2,3,5},new[]{0,6,3,5},new[]{0,1,5,4}};
        static readonly string[][] Voices={
            new[]{"Soft keys","Glass bell","Round bass"},
            new[]{"Warm pad","Pluck","Analog bass"},
            new[]{"String pad","Mallet","Sub bass"},
            new[]{"Soft keys","Flute synth","Round bass"},
            new[]{"Dark pad","Bright stack","Reese bass"}
        };
        const double Tau=2*Math.PI;
        public static double Clamp(double x,double min,double max) { return Math.Max(min,Math.Min(max,x)); }
        static double Hertz(int note) { return 440*Math.Pow(2,(note-69)/12.0); }
        static double Median(IEnumerable<double> input) {
            var values=input.OrderBy(x=>x).ToArray();
            return (values[values.Length/2]+values[(values.Length-1)/2])*.5;
        }

        public static Profile Summarize(IList<Reference> tracks) {
            var p=new Profile();
            if(tracks.Count==0) return p;
            var tempo=tracks.Where(t=>t.Tempo>0).Select(t=>t.Tempo).ToArray();
            p.TempoCount=tempo.Length;
            if(tempo.Length>0) p.Tempo=(int)Math.Round(Median(tempo));
            p.Energy=Median(tracks.Select(t=>t.Energy));
            p.Brightness=Median(tracks.Select(t=>t.Brightness));
            double[] chroma=new double[12];
            foreach(var track in tracks) {
                double sum=track.Chroma.Sum();
                if(sum>0) for(int i=0;i<12;i++) chroma[i]+=track.Chroma[i]/sum;
            }
            double[][] weights={
                new[]{6.35,2.23,3.48,2.33,4.38,4.09,2.52,5.19,2.39,3.66,2.29,2.88},
                new[]{6.33,2.68,3.52,5.38,2.60,3.53,2.54,4.75,3.98,2.69,3.34,3.17}
            };
            double best=double.NegativeInfinity;
            for(int mode=0;mode<2;mode++) for(int root=0;root<12;root++) {
                double mean=weights[mode].Average(), dot=0, norm=0;
                for(int i=0;i<12;i++) {
                    double w=weights[mode][i]-mean;
                    dot+=w*chroma[(root+i)%12]; norm+=w*w;
                }
                double score=dot/Math.Sqrt(norm);
                if(score>best) {best=score; p.Root=root; p.Minor=mode==1;}
            }
            return p;
        }

        public static Reference ReadReference(string path) {
            int rate;
            float[] data;
            try { data=ReadWave(path,out rate); }
            catch(InvalidDataException) {
                string temporary=Path.Combine(Path.GetTempPath(),"soundprint-"+Guid.NewGuid().ToString("N")+".wav");
                try {
                    var start=new ProcessStartInfo("ffmpeg","-nostdin -hide_banner -loglevel error -y -i "+Quote(path)+" -t 48 -vn -ac 1 -ar 22050 -c:a pcm_s16le "+Quote(temporary));
                    start.UseShellExecute=false; start.CreateNoWindow=true;
                    string adjacent=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffmpeg.exe");
                    if(File.Exists(adjacent)) start.FileName=adjacent;
                    Process decoder;
                    try {decoder=Process.Start(start);}
                    catch(System.ComponentModel.Win32Exception) {
                        throw new InvalidDataException("This format needs FFmpeg. Use a PCM WAV file, or place ffmpeg.exe next to Soundprint.");
                    }
                    using(decoder) {
                        if(!decoder.WaitForExit(45000)) {
                            decoder.Kill(); decoder.WaitForExit();
                            throw new InvalidDataException("Audio decoding timed out.");
                        }
                        if(decoder.ExitCode!=0) throw new InvalidDataException("FFmpeg could not decode this audio.");
                    }
                    data=ReadWave(temporary,out rate);
                } finally { if(File.Exists(temporary)) File.Delete(temporary); }
            }
            return Analyze(data,rate,Path.GetFileName(path));
        }

        static string Quote(string path) {return "\""+path+"\"";}
        static string Tag(BinaryReader reader) {return new string(reader.ReadChars(4));}
        static float[] ReadWave(string path,out int rate) {
            using(var stream=File.OpenRead(path))
            using(var r=new BinaryReader(stream,System.Text.Encoding.ASCII)) {
                rate=0;
                if(stream.Length<12 || Tag(r)!="RIFF") throw new InvalidDataException("Expected a PCM WAV.");
                r.ReadUInt32();
                if(Tag(r)!="WAVE") throw new InvalidDataException("Expected WAV audio.");
                int format=0, channels=0, bits=0, align=0;
                long start=0, bytes=0;
                while(stream.Position+8<=stream.Length) {
                    string tag=Tag(r); long size=r.ReadUInt32(), position=stream.Position;
                    if(position+size>stream.Length) throw new InvalidDataException("Truncated WAV.");
                    if(tag=="fmt " && size>=16) {
                        format=r.ReadUInt16(); channels=r.ReadUInt16(); rate=r.ReadInt32();
                        r.ReadInt32(); align=r.ReadUInt16(); bits=r.ReadUInt16();
                        if(format==65534 && size>=40) { stream.Position=position+24; format=r.ReadUInt16(); }
                    }
                    if(tag=="data") {start=position;bytes=size;}
                    if(start>0 && rate>0) break;
                    stream.Position=position+size+(size&1);
                }
                if(rate<8000 || rate>384000 || channels<1 || channels>32 || start==0 ||
                   align!=channels*(bits/8) || !(format==1 && (bits==8 || bits==16 || bits==24 || bits==32) || format==3 && bits==32))
                    throw new InvalidDataException("Unsupported WAV encoding.");
                int stride=Math.Max(1,rate/22050);
                int frames=(int)Math.Min(bytes/align,48L*rate)/stride;
                var data=new float[frames]; stream.Position=start;
                for(int i=0;i<frames;i++) {
                    double sum=0;
                    for(int c=0;c<channels;c++) {
                        if(format==3) sum+=r.ReadSingle();
                        else if(bits==8) sum+=(r.ReadByte()-128)/128.0;
                        else if(bits==16) sum+=r.ReadInt16()/32768.0;
                        else if(bits==24) {
                            int v=r.ReadByte()|r.ReadByte()<<8|r.ReadSByte()<<16;
                            sum+=v/8388608.0;
                        } else sum+=r.ReadInt32()/2147483648.0;
                    }
                    data[i]=double.IsNaN(sum)||double.IsInfinity(sum)?0:(float)Clamp(sum/channels,-1,1);
                    stream.Position+=align*(stride-1);
                }
                rate/=stride;
                return data;
            }
        }

        public static Reference Analyze(float[] samples,int rate,string name) {
            if(samples.Length<rate*2) throw new InvalidDataException("Use at least two seconds of audio.");
            int hop=rate/100;
            var envelope=new double[samples.Length/hop];
            double power=0, edge=0;
            for(int frame=0;frame<envelope.Length;frame++) {
                double local=0;
                for(int i=frame*hop;i<(frame+1)*hop;i++) {
                    double v=samples[i]; local+=v*v;
                    if(i>0) edge+=Math.Pow(v-samples[i-1],2);
                }
                power+=local; envelope[frame]=Math.Sqrt(local/hop);
            }
            if(power<1e-7) throw new InvalidDataException("This file contains silence.");
            var novelty=envelope.Select((v,i)=>i<2?0:Math.Max(0,v-envelope[i-2])).ToArray();
            double mean=novelty.Average(), best=.17, tempo=0;
            var scores=new double[110];
            int winningLag=0;
            for(int lag=33;lag<=109 && lag<novelty.Length/2;lag++) {
                double dot=0,a2=0,b2=0;
                for(int i=lag;i<novelty.Length;i++) {
                    double a=novelty[i]-mean,b=novelty[i-lag]-mean;
                    dot+=a*b; a2+=a*a;b2+=b*b;
                }
                double bpm=60.0*rate/(hop*lag);
                double score=dot/Math.Max(1e-12,Math.Sqrt(a2*b2))-.065*Math.Abs(Math.Log(bpm/110,2));
                scores[lag]=score;
                if(score>best) {best=score;tempo=Math.Round(bpm);winningLag=lag;}
            }
            if(tempo>0 && tempo<85 && winningLag/2>=33) {
                int halfLag=winningLag/2;
                int faster=scores[halfLag]>=scores[halfLag+1]?halfLag:halfLag+1;
                if(scores[faster]>.2 && scores[faster]>=best-.24)
                    tempo=Math.Round(60.0*rate/(hop*faster));
            }
            var chroma=new double[12];
            int stride=Math.Max(1,rate/5500), windowLength=1024;
            var window=Enumerable.Range(0,windowLength).Select(i=>.5-.5*Math.Cos(Tau*i/(windowLength-1))).ToArray();
            var frameData=new double[windowLength];
            var coefficients=Enumerable.Range(40,48).Select(n=>2*Math.Cos(Tau*Hertz(n)*stride/rate)).ToArray();
            for(int start=0;start+windowLength*stride<samples.Length;start+=rate/2) {
                for(int i=0;i<windowLength;i++) frameData[i]=samples[start+i*stride]*window[i];
                for(int note=0;note<coefficients.Length;note++) {
                    double k=coefficients[note],a=0,b=0;
                    foreach(double sample in frameData) {double next=sample+k*a-b;b=a;a=next;}
                    chroma[(note+40)%12]+=Math.Sqrt(Math.Max(0,a*a+b*b-k*a*b));
                }
            }
            return new Reference {Name=name,Tempo=tempo,Energy=Clamp(5*mean/envelope.Average(),0,1),
                Brightness=Clamp(Math.Sqrt(edge/power)*1.55,0,1),Chroma=chroma};
        }

        static int Note(int[] scale,int degree) {
            int octave=(int)Math.Floor(degree/7.0);
            return scale[degree-7*octave]+12*octave;
        }

        public static string Render(SongSettings settings,string path) {
            var rng=new SeedRandom(settings.Seed);
            var mix=new Mix(settings.Bars*240.0/settings.Tempo+1.6,rng);
            double beat=60.0/settings.Tempo;
            int palette=Array.IndexOf(Palettes,settings.Palette)-1;
            if(palette<0) palette=rng.Pick(settings.Brightness>.48?3:4)+(settings.Brightness>.48?1:0);
            var voices=(string[])Voices[palette].Clone();
            if(settings.Palette==Palettes[0]) {
                for(int slot=0;slot<voices.Length;slot++) {
                    int offset=rng.Pick(Voices.Length);
                    if(rng.Next()<.65) voices[slot]=Voices[(palette+offset)%Voices.Length][slot];
                }
            }
            int[] scale=Scales[settings.Minor?1:0];
            int[] progression=Progressions[rng.Pick(Progressions.Length)];
            int transpose=settings.Root;
            int section=Math.Max(2,settings.Bars/8);
            double swing=palette==3?.10:rng.Next()*.045;
            var motif=Enumerable.Range(0,8).Select(i=>rng.Pick(5)).ToArray();
            int rhythm=rng.Pick(4), inversion=rng.Pick(3), rootOctave=48+transpose;
            bool seventh=rng.Next()<.55;
            for(int bar=0;bar<settings.Bars;bar++) {
                int degree=progression[bar%progression.Length];
                if(bar==settings.Bars-1) degree=0;
                bool opening=bar<section, ending=bar>=settings.Bars-section;
                bool breakdown=bar>=settings.Bars/2 && bar<settings.Bars/2+section;
                bool chorus=!opening && !ending && !breakdown;
                double start=bar*4*beat;
                double lift=chorus?1:breakdown?.52:.7;
                for(int i=0;i<(seventh?4:3);i++) {
                    int note=rootOctave+Note(scale,degree+i*2)-(i>=inversion?12:0);
                    mix.Tone(voices[0],note,start+i*.013,beat*(bar==settings.Bars-1?4.1:3.94),.081*lift,(i-1.25)*.34);
                }
                if(!opening && !breakdown) {
                    double[] bass=chorus && (settings.Energy>.42 || palette==1)
                        ? new[]{0,1.5,2,3.25} : new[]{0,2.5};
                    foreach(double t in bass) {
                        int pitch=rootOctave-24+Note(scale,degree+(t>2 && rng.Next()<.28?4:0));
                        mix.Tone(voices[2],pitch,start+t*beat,beat*(chorus?.58:.87),.21*lift,0);
                    }
                }
                if(!breakdown && (!opening || bar>0)) {
                    double[] kicks=chorus && palette==1?new[]{0.0,1.0,2.0,3.0}:
                        chorus && rhythm%2==1?new[]{0,1.75,2.5}:new[]{0.0,2.0};
                    foreach(double t in kicks) mix.Drum(0,start+t*beat,.34*lift);
                    foreach(double t in new[]{1.0,3.0}) mix.Drum(1,start+t*beat,.16*lift);
                    int divisions=chorus && settings.Energy>.35?8:4;
                    for(int i=0;i<divisions;i++) {
                        double time=i*4.0/divisions;
                        mix.Drum(2,start+(time+(i%2==1?swing:0))*beat,(i%2==0?.067:.039)*lift);
                    }
                }
                if(opening && bar==0) continue;
                double[][] phrases={new[]{.5,1.5,2,3.25},new[]{0,.75,2,2.75,3.5},new[]{0,1.5,3},new[]{.25,1,1.75,2.5,3.25}};
                var phrase=phrases[(rhythm+(chorus?0:2))%phrases.Length];
                for(int step=0;step<phrase.Length;step++) {
                    if(!chorus && step%2==1) continue;
                    if(step>0 && rng.Next()<.16) continue;
                    int offset=motif[(step+bar%2)%motif.Length];
                    if(offset>=3) offset=step%2;
                    int pitch=60+transpose+Note(scale,degree+offset*2)+(chorus && bar>=settings.Bars/2?12:0);
                    if(pitch>83) pitch-=12;
                    double time=start+phrase[step]*beat, length=beat*(.23+rng.Next()*.36);
                    mix.Tone(voices[1],pitch,time,length,.115*lift,-.18);
                    mix.Tone(voices[1],pitch,time+.75*beat,length,.032*lift,.62);
                    mix.Tone(voices[1],pitch,time+1.5*beat,length,.015*lift,-.66);
                }
            }
            mix.Write(path);
            return String.Join(" / ",voices);
        }

        sealed class Mix {
            readonly float[] left,right;
            readonly SeedRandom random;
            public Mix(double seconds,SeedRandom random) {
                left=new float[(int)(seconds*Rate)]; right=new float[left.Length]; this.random=random;
            }
            public void Tone(string instrument,int pitch,double at,double length,double level,double pan) {
                int offset=(int)(at*Rate), count=Math.Min((int)(length*Rate),left.Length-offset);
                double frequency=Hertz(pitch), step=Tau*frequency/Rate, gl=Math.Sqrt((1-pan)/2),gr=Math.Sqrt((1+pan)/2);
                bool pad=instrument.EndsWith("pad",StringComparison.Ordinal);
                double attack=pad?.13:.009, decay=pad?.4:.15;
                for(int i=0;i<count;i++) {
                    double p=i*step,t=(double)i/Rate,s=Math.Sin(p), v;
                    switch(instrument) {
                        case "Glass bell": v=.65*s+.24*Math.Sin(p*2.01)*Math.Exp(-7*t)+.1*Math.Sin(p*3.99)*Math.Exp(-13*t); break;
                        case "Pluck": v=.65*s+.22*Math.Sin(p*2)*Math.Exp(-11*t)+.13*Math.Sin(p*3)*Math.Exp(-15*t); break;
                        case "Mallet": v=.89*s+.17*Math.Sin(p*4)*Math.Exp(-14*t); break;
                        case "Flute synth": v=.9*Math.Sin(p+.17*Math.Sin(Tau*4.7*t))+.09*Math.Sin(p*2); break;
                        case "Bright stack": v=.54*s+.22*Math.Sin(p*2)+.11*Math.Sin(p*3)+.12*Math.Sin(p*1.004); break;
                        case "Warm pad": v=.66*s+.21*Math.Sin(p*1.003)+.11*Math.Sin(p*2); break;
                        case "String pad": v=.56*s+.18*Math.Sin(p*2)+.14*Math.Sin(p*3)+.09*Math.Sin(p*.997); break;
                        case "Dark pad": v=.68*s+.24*Math.Sin(p*.996)+.08*Math.Sin(p*2); break;
                        case "Analog bass": v=.73*s+.16*Math.Sin(p*2)+.1*Math.Sin(p*3); break;
                        case "Sub bass": v=.96*s+.04*Math.Sin(p*2); break;
                        case "Reese bass": v=.57*s+.25*Math.Sin(p*1.009)+.13*Math.Sin(p*2); break;
                        case "Round bass": v=.87*s+.12*Math.Sin(p*3); break;
                        default: v=.81*s+.16*Math.Sin(p*2)*Math.Exp(-4*t)+.05*Math.Sin(p*3); break;
                    }
                    double envelope=Math.Min(1,t/attack)*Math.Min(1,(length-t)/decay);
                    if(!pad && !instrument.EndsWith("bass",StringComparison.Ordinal)) envelope*=Math.Exp(-2.2*t/length);
                    double sample=v*level*envelope;
                    left[offset+i]+=(float)(sample*gl);right[offset+i]+=(float)(sample*gr);
                }
            }
            public void Drum(int type,double at,double gain) {
                int offset=(int)(at*Rate), count=Math.Min((int)((type==0?.24:type==1?.18:.071)*Rate),left.Length-offset);
                double phase=0, previous=0;
                for(int i=0;i<count;i++) {
                    double t=(double)i/Rate, noise=random.Next()*2-1,sample;
                    if(type==0) {
                        phase+=Tau*(43+94*Math.Exp(-34*t))/Rate;
                        sample=Math.Sin(phase)*Math.Exp(-17*t)+noise*.09*Math.Exp(-210*t);
                    } else if(type==1) {
                        sample=(.65*(noise-previous)+.28*Math.Sin(Tau*171*t)*Math.Exp(-24*t))*Math.Exp(-27*t);
                    } else sample=(noise-previous)*.65*Math.Exp(-67*t);
                    previous=noise;
                    float v=(float)(gain*sample);
                    left[offset+i]+=v;right[offset+i]+=type==2?v*.7f:v;
                }
            }
            public void Write(string file) {
                double peak=.00001;
                for(int i=0;i<left.Length;i++) peak=Math.Max(peak,Math.Max(Math.Abs(left[i]),Math.Abs(right[i])));
                double gain=Math.Min(2.4,.91/peak);
                using(var writer=new BinaryWriter(File.Create(file))) {
                    writer.Write(new[]{'R','I','F','F'}); writer.Write(36+left.Length*4);
                    writer.Write(new[]{'W','A','V','E','f','m','t',' '}); writer.Write(16);
                    writer.Write((short)1); writer.Write((short)2); writer.Write(Rate);
                    writer.Write(Rate*4); writer.Write((short)4); writer.Write((short)16);
                    writer.Write(new[]{'d','a','t','a'});writer.Write(left.Length*4);
                    for(int i=0;i<left.Length;i++) {writer.Write((short)(left[i]*gain*32767));writer.Write((short)(right[i]*gain*32767));}
                }
            }
        }
    }
}
