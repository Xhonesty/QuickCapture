using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuickCapture;
internal static class VideoCutTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Video deletion normalization, frame boundaries, adjacent ranges and empty prevention", () =>
        {
            var cuts = VideoCuts.Normalize(new[] { new VideoRange(1.01, 2), new VideoRange(1.5, 2.5), new VideoRange(2.5, 3), new VideoRange(-1, .1) }, 4, 30);
            UpgradeTests.Ensure(cuts.Length == 2 && cuts[0] == new VideoRange(0,.1) && cuts[1] == new VideoRange(1,3), "Overlaps/adjacency not normalized");
            var kept = VideoCuts.Kept(.5, 3.5, cuts); UpgradeTests.Ensure(kept.SequenceEqual(new[] { new VideoRange(.5,1),new VideoRange(3,3.5) }), "Kept source ranges wrong");
            UpgradeTests.Ensure(VideoCuts.Next(1.5,.5,3.5,cuts) == 3 && VideoCuts.Next(3.6,.5,3.5,cuts) == 3.5, "Preview doesn't skip cuts"); return Task.CompletedTask;
        });
        string directory = Path.Combine(Paths.TestRoot!, "VideoCuts"); Directory.CreateDirectory(directory); var tools = await MediaTools.DetectAsync(new Settings());
        string source = Path.Combine(directory, "colors-"+Guid.NewGuid().ToString("N")+".mp4");
        await MediaTools.RunAsync(tools.Ffmpeg!, new[] { "-nostdin","-v","error","-y","-f","lavfi","-i","color=red:s=320x180:r=30:d=1","-f","lavfi","-i","color=lime:s=320x180:r=30:d=1","-f","lavfi","-i","color=blue:s=320x180:r=30:d=1","-f","lavfi","-i","sine=frequency=440:duration=3","-filter_complex","[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]","-map","[v]","-map","3:a","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-shortest",source },CancellationToken.None);
        var info = await VideoExportService.ProbeAsync(source,tools); byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        await check("Multi-cut MP4/WebM/GIF concatenation with crop/speed/mute, frame sequence, audio sync and cancellation", async () =>
        {
            var cuts = new[] { new VideoRange(.5,1), new VideoRange(1.5,2) };
            foreach (var format in Enum.GetValues<RecordingFormat>())
            foreach (bool mute in new[] { false, true })
            {
                string target = Path.Combine(directory, $"cut-{format}-{mute}."+VideoExportService.Extension(format));
                var request = new VideoExportRequest(source,target,format,ExportQuality.High,0,3,1.5,mute,15,Overwrite:true,Crop:new VideoCrop(20,10,240,140),FramesPerSecond:15,Deleted:cuts);
                await VideoExportService.ExportAsync(request,info,tools); var result = await VideoExportService.ProbeAsync(target,tools);
                UpgradeTests.Ensure(Math.Abs(result.Duration-2/1.5)<.12 && result.Width==240 && result.Height==140 && result.HasAudio == (!mute && format!=RecordingFormat.Gif), "Output duration/crop/audio differs");
                var decode = await MediaTools.RunAsync(tools.Ffmpeg!,new[]{"-v","error","-i",target,"-f","null","-"},CancellationToken.None); UpgradeTests.Ensure(decode.ExitCode==0 && string.IsNullOrWhiteSpace(decode.Error),"Export has decoding errors");
                foreach (var sample in new[] { (time:"0.15",channel:2),(time:"0.5",channel:1),(time:"1.0",channel:0) })
                {
                    string png=Path.Combine(directory,"sample.png"); await MediaTools.RunAsync(tools.Ffmpeg!,new[]{"-v","error","-y","-ss",sample.time,"-i",target,"-frames:v","1",png},CancellationToken.None);
                    var bitmap=CaptureService.Load(png); byte[] bytes=UpgradeTests.Bytes(bitmap); int offset=(bitmap.PixelHeight/2*bitmap.PixelWidth+bitmap.PixelWidth/2)*4;
                    UpgradeTests.Ensure(bytes[offset+sample.channel]>180 && Enumerable.Range(0,3).Where(c=>c!=sample.channel).All(c=>bytes[offset+c]<65),"Wrong concatenated frame order / black frame");
                }
                if (result.HasAudio)
                {
                    var probe=await MediaTools.RunAsync(tools.Ffprobe!,new[]{"-v","error","-show_entries","stream=codec_type,duration","-of","json",target},CancellationToken.None);
                    File.WriteAllText(Path.Combine(directory,$"sync-{format}.json"),probe.Output);
                    if (format==RecordingFormat.Mp4) { using var doc=System.Text.Json.JsonDocument.Parse(probe.Output); var durations=doc.RootElement.GetProperty("streams").EnumerateArray().Select(s=>double.Parse(s.GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture)).ToArray(); UpgradeTests.Ensure(durations.Max()-durations.Min()<.1,"Audio/video drift after cut"); }
                }
            }
            bool empty=false; try { VideoExportService.Validate(new(source,Path.Combine(directory,"empty.mp4"),RecordingFormat.Mp4,ExportQuality.Medium,0,3,Deleted:new[]{new VideoRange(0,3)}),info,tools); } catch(ArgumentException) { empty=true; } UpgradeTests.Ensure(empty,"Empty export allowed");
            using var cancel=new CancellationTokenSource(); cancel.Cancel(); try { await VideoExportService.ExportAsync(new(source,Path.Combine(directory,"cancel.mp4"),RecordingFormat.Mp4,ExportQuality.Medium,0,3,Deleted:cuts),info,tools,cancellationToken:cancel.Token); } catch(OperationCanceledException) { }
            UpgradeTests.Ensure(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hash) && !Directory.GetFiles(directory,"*.partial*").Any(),"Cancellation changed original or retained temporary output");
        });
        await check("Video editing history, configuration reopen, legacy metadata and both-theme cut UI", async () =>
        {
            var owner=new Window(); owner.Show();
            try
            {
                var settings=new Settings{OutputDirectory=directory}; var metadata=new RecordingMetadata(ExportQuality.Medium,30,true,3);
                var dialog=new RecordingEditWindow(owner,source,settings,metadata);
                dialog.Show(); await dialog.Initialization; dialog.SetCuts(new[]{new VideoRange(1,2)}); dialog.SetCuts(new[]{new VideoRange(.5,2)}); dialog.UndoEdit(); UpgradeTests.Ensure(dialog.Deleted.Single()==new VideoRange(1,2),"Cut undo wrong"); dialog.RedoEdit(); UpgradeTests.Ensure(dialog.Deleted.Single()==new VideoRange(.5,2),"Cut redo wrong");
                dialog.Timeline.SetRange(.1,2.9); await dialog.ConfigurationSaved;
                foreach(string theme in new[]{"Light","Dark"}) { ThemeService.Apply(theme); UpgradeTests.Find<Expander>((DependencyObject)dialog.Content,_=>true).IsExpanded=true; dialog.UpdateLayout(); UiChangeTests.Render(dialog,"video-cuts-"+theme.ToLowerInvariant()+".png"); }
                dialog.Close();
                var reopen=new RecordingEditWindow(owner,source,settings,metadata); reopen.Show(); await reopen.Initialization;
                UpgradeTests.Ensure(reopen.Deleted.Single()==new VideoRange(.5,2) && Math.Abs(reopen.Timeline.Start-.1)<.001 && Math.Abs(reopen.Timeline.End-2.9)<.001 && reopen.PreserveMaster,"Reopened edit lost original coordinates"); reopen.Close();
                string legacy=RecordingRecovery.NewMaster(); File.Copy(source,legacy); File.WriteAllText(legacy+".json","{\"Quality\":1,\"Fps\":30,\"HasAudio\":true,\"Duration\":3}"); UpgradeTests.Ensure(RecordingRecovery.Load(legacy).Crop==null,"Old metadata incompatible"); RecordingRecovery.Remove(legacy);
                string future=RecordingRecovery.NewMaster();File.Copy(source,future);RecordingRecovery.Remember(future,new(ExportQuality.Medium,30,true,3,Version:99));byte[] futureConfig=File.ReadAllBytes(future+".json");
                var unsupported=new RecordingEditWindow(owner,future,settings,RecordingRecovery.Load(future));unsupported.Show();await unsupported.Initialization;unsupported.BeginCrop();unsupported.ConfirmCrop();await unsupported.ExportAsync();
                UpgradeTests.Ensure(!UpgradeTests.Find<Button>(unsupported,b=>b.Name=="ExportRecording").IsEnabled&&!UpgradeTests.Find<Button>(unsupported,b=>b.Name=="EditVideoCrop").IsEnabled&&File.ReadAllBytes(future+".json").SequenceEqual(futureConfig),"Unsupported recording configuration was overwritten");unsupported.Close();RecordingRecovery.Remove(future);
            }
            finally { owner.Close(); }
        });
        await check("Cut preview actually skips deleted frames; UI export cancellation/failure retain edits and success preserves owned master",async()=>
        {
            string master=RecordingRecovery.NewMaster();File.Copy(source,master);RecordingRecovery.Remember(master,new(ExportQuality.Medium,30,true,3));
            var main=new MainWindow();main.Show();var done=new TaskCompletionSource();
            _=Application.Current.Dispatcher.BeginInvoke(new Action(async()=>
            {
                RecordingEditWindow? dialog=null;
                try
                {
                    await Task.Delay(100);dialog=Application.Current.Windows.OfType<RecordingEditWindow>().Single(w=>w.IsVisible);await dialog.Initialization;dialog.SetCuts(new[]{new VideoRange(1,2)});await dialog.ConfigurationSaved;
                    var preview=UpgradeTests.Find<MediaElement>(dialog,b=>b.Name=="RecordingPreview");for(int n=0;n<60&&preview.NaturalVideoWidth==0;n++)await Task.Delay(50);
                    typeof(RecordingEditWindow).GetMethod("Seek",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,new object[]{.95});
                    var play=UpgradeTests.Find<Button>(dialog,b=>b.Name=="PlaySelection");play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(350);UpgradeTests.Ensure(preview.Position.TotalSeconds>=2&&preview.Position.TotalSeconds<2.8,"Actual preview did not skip middle deletion");play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    UpgradeTests.Find<ComboBox>(dialog,b=>b.Name=="RecordingFormat").SelectedIndex=(int)RecordingFormat.WebM;
                    var filename=UpgradeTests.Find<TextBox>(dialog,b=>b.Name=="RecordingFilename");filename.Text="invalid/name";await dialog.ExportAsync();UpgradeTests.Ensure(dialog.SavedPath==null&&File.Exists(master),"Failed UI export lost source");
                    filename.Text="cut-ui-"+Guid.NewGuid().ToString("N")+".webm";
                    var cancel=UpgradeTests.Find<Button>(dialog,b=>b.Name=="CancelExport");Task cancelled=dialog.ExportAsync();for(int n=0;n<100&&cancel.Content as string!="取消导出";n++)await Task.Delay(5);cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await cancelled;
                    UpgradeTests.Ensure(dialog.IsVisible&&dialog.SavedPath==null&&dialog.Deleted.Count==1&&(await RecordingEditStore.LoadAsync(master))!.Deleted.Length==1,"Cancellation lost edit state");
                    await dialog.ExportAsync();UpgradeTests.Ensure(dialog.SavedPath!=null&&File.Exists(dialog.SavedPath),"Cut UI retry failed");done.SetResult();
                }
                catch(Exception ex){done.SetException(ex);}
                finally{if(dialog?.IsVisible==true)dialog.Close();}
            }));
            try
            {
                typeof(MainWindow).GetMethod("EditRecording",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,new object[]{master});await done.Task;
                UpgradeTests.Ensure(File.Exists(master)&&File.Exists(RecordingEditStore.FileFor(master)),"Successful middle cut removed editable master/config");
                var reopened=new RecordingEditWindow(main,master,new Settings(),RecordingRecovery.Load(master));reopened.Show();await reopened.Initialization;UpgradeTests.Ensure(reopened.Deleted.Single()==new VideoRange(1,2),"Retained master reopened without cut");reopened.Close();
            }
            finally{main.Hide();}
        });
    }
}
