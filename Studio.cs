using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Soundprint {
    static class Program {
        [STAThread]
        static void Main(string[] args) {
            if(args.Length==3 && args[0]=="--import-check") {
                try {
                    var imported=Engine.ReadReference(args[1]);
                    File.WriteAllText(args[2],"PASS: "+imported.Name+" decoded and analyzed at "+imported.Tempo+" BPM.");
                } catch(Exception ex) {
                    File.WriteAllText(args[2],ex.ToString());
                    Environment.ExitCode=1;
                }
                return;
            }
            if(args.Length==2 && args[0]=="--smoke") {
                try {Smoke(args[1]);}
                catch(Exception ex) {
                    File.WriteAllText(Path.Combine(args[1],"failure.txt"),ex.ToString());
                    Environment.ExitCode=1;
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Studio());
        }

        static void Smoke(string output) {
            Directory.CreateDirectory(output);
            var settings=new SongSettings {Tempo=120,Bars=8,Root=0,Minor=false,Energy=.8,Brightness=.6,Palette="Club",Seed=42};
            string a=Path.Combine(output,"first.wav"), b=Path.Combine(output,"repeat.wav"), c=Path.Combine(output,"different.wav");
            Engine.Render(settings,a); Engine.Render(settings,b); settings.Seed=43; Engine.Render(settings,c);
            byte[] first=File.ReadAllBytes(a);
            if(!first.SequenceEqual(File.ReadAllBytes(b)) || first.SequenceEqual(File.ReadAllBytes(c)))
                throw new InvalidDataException("Seed reproducibility failed.");
            if(BitConverter.ToInt32(first,40)!=first.Length-44)
                throw new InvalidDataException("Invalid WAV length.");
            Reference r=Engine.ReadReference(a);
            var p=Engine.Summarize(new[]{r});
            if(Math.Abs(p.Tempo-120)>5 || p.TempoCount!=1)
                throw new InvalidDataException("Generated track did not analyze at the expected tempo: "+p.Tempo);
            using(var player=new SoundPlayer(a)) player.Load();
            using(var window=new Studio()) {
                if(window.Text!="Soundprint Studio") throw new InvalidDataException("Window failed to initialize.");
            }
            File.WriteAllText(Path.Combine(output,"pass.txt"),"PASS: 120 BPM, deterministic rerender, distinct variation, valid stereo WAV, matching reference tempo, Windows playback loader, desktop window initialization.");
        }
    }

    sealed class Studio : Form {
        static readonly Color Background=Color.FromArgb(17,20,18), PanelColor=Color.FromArgb(27,33,28);
        static readonly Color TextColor=Color.FromArgb(244,248,236), Muted=Color.FromArgb(189,199,183);
        static readonly Color Accent=Color.FromArgb(212,247,128);
        readonly List<Reference> references=new List<Reference>();
        readonly ListBox library=new ListBox();
        readonly Label profileText=new Label(), status=new Label(), take=new Label();
        readonly NumericUpDown bpm=new NumericUpDown();
        readonly ComboBox length=new ComboBox(), palette=new ComboBox(), key=new ComboBox(), mode=new ComboBox();
        readonly TextBox seed=new TextBox();
        readonly Button add, remove, generate, vary, play, stop, save;
        readonly SoundPlayer player=new SoundPlayer();
        string recording;
        ulong recordedSeed;
        bool busy;

        public Studio() {
            Text="Soundprint Studio";
            StartPosition=FormStartPosition.CenterScreen;
            ClientSize=new Size(970,735); MinimumSize=new Size(760,715);
            BackColor=Background; ForeColor=TextColor; Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Dpi;

            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(25),ColumnCount=1,RowCount=5};
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,94));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,166));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,110));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            Controls.Add(layout);
            var title=Section();
            title.BackColor=Background;
            Heading(title,"Soundprint Studio",27);
            Copy(title,"Compose a new instrumental from your playlist, or start from scratch.");
            layout.Controls.Add(title,0,0);

            var split=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,0,0,12)};
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,58));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,42));
            layout.Controls.Add(split,0,1);
            var refs=Section(); refs.Margin=new Padding(0,0,10,0); split.Controls.Add(refs,0,0);
            Heading(refs,"Reference tracks",15);
            Copy(refs,"Drop audio files or an M3U playlist of local tracks.");
            add=Action("Add audio or playlist");
            remove=Action("Remove selected");
            add.Click+=async (s,e)=>await Choose();
            remove.Click+=(s,e)=>{ if(library.SelectedIndex>=0) {references.RemoveAt(library.SelectedIndex);RefreshProfile();} };
            Row(refs,add,remove);
            library.Size=new Size(440,103); library.Margin=new Padding(0,7,0,0);
            library.BackColor=Background; library.ForeColor=TextColor; library.BorderStyle=BorderStyle.FixedSingle;
            library.SelectedIndexChanged+=(s,e)=>UpdateButtons();
            refs.SizeChanged+=(s,e)=>{library.Width=Math.Max(200,refs.ClientSize.Width-32); library.Height=Math.Max(45,refs.ClientSize.Height-126);};
            refs.Controls.Add(library);

            var profile=Section(); profile.Margin=Padding.Empty; split.Controls.Add(profile,1,0);
            Heading(profile,"Playlist analysis",15);
            profileText.ForeColor=Muted; profileText.AutoSize=true; profileText.MaximumSize=new Size(280,0);
            profile.SizeChanged+=(s,e)=>profileText.MaximumSize=new Size(Math.Max(160,profile.ClientSize.Width-31),0);
            profile.Controls.Add(profileText);

            var maker=Section(); maker.Margin=new Padding(0,0,0,10);
            Heading(maker,"Composition",15);
            layout.Controls.Add(maker,0,2);
            bpm.Minimum=55;bpm.Maximum=190;bpm.Value=110;bpm.Width=70;bpm.TextAlign=HorizontalAlignment.Center;
            SetOptions(length,new[]{"8 bars","16 bars","32 bars","64 bars"},1,82);
            SetOptions(palette,Engine.Palettes,0,142);
            SetOptions(key,Engine.Notes,0,65);
            SetOptions(mode,new[]{"Major","Minor"},1,80);
            seed.Width=145; seed.AccessibleName="Song seed";
            Row(maker,Field("BPM",bpm),Field("Length",length),Field("Key",key),Field("Mode",mode),Field("Palette",palette),Field("Seed (optional)",seed));
            generate=Action("Generate track",true); vary=Action("New variation");
            generate.Click+=async (s,e)=>await Generate(false);
            vary.Click+=async (s,e)=>await Generate(true);
            Row(maker,generate,vary);

            var export=Section(); export.Margin=new Padding(0,0,0,8); layout.Controls.Add(export,0,3);
            take.Text="Your rendered track will appear here."; take.AutoSize=true; take.ForeColor=TextColor;
            export.Controls.Add(take);
            play=Action("Play");stop=Action("Stop");save=Action("Save WAV");
            play.Click+=(s,e)=>{
                try {player.Play(); status.Text="Playing the rendered track.";}
                catch(Exception ex) {status.Text=ex.Message;}
            };
            stop.Click+=(s,e)=>{player.Stop();status.Text="Playback stopped.";};
            save.Click+=(s,e)=>SaveTrack();
            Row(export,play,stop,save);
            status.Dock=DockStyle.Fill;status.ForeColor=Muted;status.AutoEllipsis=true;
            status.Text="Ready. Add references or generate a track with the default settings.";
            layout.Controls.Add(status,0,4);
            AllowDrop=true; refs.AllowDrop=true;library.AllowDrop=true;
            foreach(Control target in new Control[]{this,refs,library}) {
                target.DragEnter+=(s,e)=>{e.Effect=!busy && e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;};
                target.DragDrop+=async (s,e)=>{var paths=e.Data.GetData(DataFormats.FileDrop) as string[];if(paths!=null) await Import(paths);};
            }
            FormClosing+=(s,e)=>{if(busy) {e.Cancel=true;status.Text="Wait for the current operation to finish before closing.";return;}player.Stop();player.Dispose();DeleteRecording(recording);};
            RefreshProfile();
        }

        static FlowLayoutPanel Section() {return new FlowLayoutPanel{Dock=DockStyle.Fill,BackColor=PanelColor,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(15,10,15,8)};}
        static Button Action(string name,bool primary=false) {
            var button=new Button{Text=name,AutoSize=true,Height=33,Padding=new Padding(8,0,8,0),FlatStyle=FlatStyle.Flat,
                BackColor=primary?Accent:Color.FromArgb(41,49,39),ForeColor=primary?Background:TextColor,Cursor=Cursors.Hand,Margin=new Padding(0,3,8,0)};
            button.FlatAppearance.BorderColor=primary?Accent:Color.FromArgb(77,87,71);
            return button;
        }
        static void Heading(Control parent,string text,int size) {
            parent.Controls.Add(new Label{Text=text,AutoSize=true,Font=new Font("Segoe UI",size,FontStyle.Bold),Margin=Padding.Empty});
        }
        static void Copy(Control parent,string text) {parent.Controls.Add(new Label{Text=text,AutoSize=true,ForeColor=Muted,Margin=new Padding(0,3,0,4)});}
        static void Row(Control parent,params Control[] children) {
            var row=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Margin=Padding.Empty};
            row.Controls.AddRange(children);parent.Controls.Add(row);
        }
        static Control Field(string label,Control input) {
            var field=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(0,0,8,0)};
            Copy(field,label);input.Margin=Padding.Empty;input.AccessibleName=label;
            field.Controls.Add(input);return field;
        }
        static void SetOptions(ComboBox input,string[] items,int selected,int width) {
            input.DropDownStyle=ComboBoxStyle.DropDownList; input.Items.AddRange(items);input.SelectedIndex=selected;input.Width=width;
        }
        static bool IsPlaylist(string path) {
            string ext=Path.GetExtension(path);
            return ext.Equals(".m3u",StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8",StringComparison.OrdinalIgnoreCase);
        }
        static IEnumerable<string> Entries(string path) {
            if(!IsPlaylist(path)) {yield return path;yield break;}
            string home=Path.GetDirectoryName(Path.GetFullPath(path));
            foreach(string raw in File.ReadLines(path)) {
                string line=raw.Trim();
                if(line.Length==0 || line.StartsWith("#")) continue;
                Uri uri;
                if(Uri.TryCreate(line,UriKind.Absolute,out uri) && uri.IsFile) yield return uri.LocalPath;
                else if(line.IndexOf("://",StringComparison.Ordinal)>=0) yield return line;
                else yield return Path.Combine(home,line);
            }
        }
        async Task Choose() {
            using(var dialog=new OpenFileDialog {Multiselect=true,Title="Choose reference audio or a local playlist",
                Filter="Audio or playlist|*.wav;*.mp3;*.m4a;*.flac;*.ogg;*.aac;*.opus;*.aiff;*.wma;*.m3u;*.m3u8|All files|*.*"}) {
                if(dialog.ShowDialog(this)==DialogResult.OK) await Import(dialog.FileNames);
            }
        }
        async Task Import(string[] paths) {
            if(busy) return;
            Busy(true);
            int count=0;
            var errors=new List<string>();
            try {
                foreach(string file in paths) {
                    string[] items;
                    try {items=Entries(file).ToArray();}
                    catch(Exception ex) {errors.Add(Path.GetFileName(file)+": "+ex.Message);continue;}
                    foreach(string path in items) {
                        if(IsPlaylist(path) || path.IndexOf("://",StringComparison.Ordinal)>=0) {
                            errors.Add("Playlist entries must point to local audio files."); continue;
                        }
                        status.Text="Analyzing "+Path.GetFileName(path)+"...";
                        try {references.Add(await Task.Run(()=>Engine.ReadReference(path)));count++;}
                        catch(Exception ex) {errors.Add(Path.GetFileName(path)+": "+ex.Message);}
                    }
                }
                if(count>0) RefreshProfile();
                status.Text=count+" track(s) added"+(errors.Count==0?".":". "+errors.Count+" skipped.");
                if(errors.Count>0) MessageBox.Show(this,String.Join(Environment.NewLine,errors.Take(12)), "Reference import",MessageBoxButtons.OK,MessageBoxIcon.Information);
            } finally {Busy(false);}
        }
        void RefreshProfile() {
            library.BeginUpdate(); library.Items.Clear();
            foreach(Reference r in references) library.Items.Add(r.Name+"   |   "+(r.Tempo>0?r.Tempo+" BPM":"Tempo unclear"));
            library.EndUpdate();
            var p=Engine.Summarize(references);
            if(references.Count==0) profileText.Text="Add tracks to estimate tempo, key, rhythmic energy and brightness.";
            else profileText.Text=references.Count+" reference track(s)"+Environment.NewLine+
                (p.TempoCount>0?"Tempo   "+p.Tempo+" BPM":"Tempo   Unclear (110 BPM starting point)")+Environment.NewLine+
                "Estimated key   "+Engine.Notes[p.Root]+(p.Minor?" minor":" major")+Environment.NewLine+
                "Rhythmic energy   "+(p.Energy>.65?"High":p.Energy>.35?"Medium":"Low")+Environment.NewLine+
                "Timbre   "+(p.Brightness>.48?"Bright":"Warm")+Environment.NewLine+Environment.NewLine+
                "The composer uses tempo and key to set the track, and uses energy and timbre to shape its drums and synths.";
            bpm.Value=Math.Min(bpm.Maximum,Math.Max(bpm.Minimum,p.Tempo));
            key.SelectedIndex=p.Root;mode.SelectedIndex=p.Minor?1:0;
            UpdateButtons();
        }
        static ulong NewSeed() {
            var bytes=new byte[8];
            using(var random=RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToUInt64(bytes,0);
        }
        async Task Generate(bool fresh) {
            if(busy) return;
            ulong id;
            if(fresh || String.IsNullOrWhiteSpace(seed.Text)) id=NewSeed();
            else if(!UInt64.TryParse(seed.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out id)) {
                status.Text="Enter a whole-number seed from 0 to 18446744073709551615 or leave it blank.";seed.Focus();return;
            }
            seed.Text=id.ToString(CultureInfo.InvariantCulture);
            var profile=Engine.Summarize(references);
            var song=new SongSettings {Tempo=(int)bpm.Value,Bars=8<<length.SelectedIndex,Root=key.SelectedIndex,Minor=mode.SelectedIndex==1,
                Palette=(string)palette.SelectedItem,Energy=profile.Energy,Brightness=profile.Brightness,Seed=id};
            string file=Path.Combine(Path.GetTempPath(),"soundprint-"+Guid.NewGuid().ToString("N")+".wav");
            Busy(true);status.Text="Composing and rendering stereo audio...";
            try {
                string instruments=await Task.Run(()=>Engine.Render(song,file));
                player.Stop();
                player.SoundLocation=file;
                DeleteRecording(recording);
                recording=file;
                recordedSeed=id;
                take.Text=song.Tempo+" BPM  |  "+song.Bars+" bars  |  "+instruments;
                status.Text="Track ready. Play it or save the stereo WAV.";
            } catch(Exception ex) {DeleteRecording(file);status.Text="Render failed. "+ex.Message;}
            finally {Busy(false);}
        }
        void SaveTrack() {
            using(var dialog=new SaveFileDialog {Title="Save your instrumental", Filter="Stereo WAV|*.wav",FileName="Soundprint-"+recordedSeed+".wav"}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                try {File.Copy(recording,dialog.FileName,true);status.Text="Saved "+Path.GetFileName(dialog.FileName);}
                catch(Exception ex) {status.Text="Save failed. "+ex.Message;}
            }
        }
        static void DeleteRecording(string path) {
            if(path==null) return;
            try {File.Delete(path);} catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        void Busy(bool value) {busy=value;UseWaitCursor=value;UpdateButtons();}
        void UpdateButtons() {
            if(save==null) return;
            add.Enabled=generate.Enabled=vary.Enabled=!busy;
            remove.Enabled=!busy && library.SelectedIndex>=0;
            play.Enabled=stop.Enabled=save.Enabled=!busy && recording!=null;
        }
    }
}
