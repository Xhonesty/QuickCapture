using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture;
internal static class ScreenshotProjectTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check, bool restartOnly = false)
    {
        string directory=Path.Combine(Paths.TestRoot!,"ScreenshotProjectsTests"); Directory.CreateDirectory(directory);
        string png=Path.Combine(directory,"all-tools.png");
        if(restartOnly)
        {
            await check("Screenshot project restores after a separate process restart and updates its export", async()=>
            {
                var loaded=await ScreenshotProjects.LoadAsync(png); UpgradeTests.Ensure(loaded.Annotations.Length==7 && loaded.Crop==new Int32Rect(12,10,420,260),"Restart lost persisted objects/crop");
                var window=new EditorWindow(loaded.Original,new Settings{OutputDirectory=directory},_=>{},loaded); window.Show();
                try { window.Editor.Surface.SetCrop(new(8,8,430,270)); string result=window.Editor.SaveImage(); UpgradeTests.Ensure(result==png && (await ScreenshotProjects.LoadAsync(png)).Crop==new Int32Rect(8,8,430,270),"Restart save did not update project/image"); }
                finally { window.Close(); }
            }); return;
        }
        var pixels=new byte[480*300*4];for(int y=0;y<300;y++)for(int x=0;x<480;x++){int i=(y*480+x)*4;pixels[i]=(byte)(x%255);pixels[i+1]=(byte)(y%255);pixels[i+2]=190;pixels[i+3]=255;}
        var original=BitmapSource.Create(480,300,96,96,PixelFormats.Bgra32,null,pixels,480*4);original.Freeze();
        var surface=new AnnotationSurface(original);
        surface.Add(new(AnnotationTool.Arrow,new(30,40),new(130,80),Colors.Red,Width:5));
        surface.Add(new(AnnotationTool.Rectangle,new(180,30),new(245,95),Colors.Teal,Width:4,Shape:AnnotationShape.Diamond,FillColor:Color.FromArgb(120,25,200,90)));
        surface.Add(new(AnnotationTool.Text,new(30,110),new(30,110),Colors.White,"中文 English\n多行",FontFamily:"Microsoft YaHei UI",FontSize:22,Bold:true,Alignment:TextAlignment.Center,TextWidth:170));
        surface.Add(new(AnnotationTool.Step,new(260,40),new(306,86),Colors.Orange,StepNumber:7));
        surface.Add(new(AnnotationTool.Freehand,new(240,145),new(360,175),Colors.Blue,Points:new[]{new Point(240,145),new Point(280,165),new Point(360,175)},Width:7));
        surface.Add(new(AnnotationTool.Mosaic,new(340,25),new(410,90),Colors.Black));
        surface.Add(new(AnnotationTool.MosaicBrush,new(240,210),new(390,240),Colors.Black,Points:new[]{new Point(240,210),new Point(320,220),new Point(390,240)},Width:16));
        surface.SetCrop(new(12,10,420,260));var rendered=surface.Export();var project=ScreenshotProjects.Snapshot(surface,8,ScreenshotFormat.Png,90);
        await check("Screenshot versioned projects round-trip every annotation, text style, numbering, crop and four standard image formats",async()=>
        {
            foreach(var format in Enum.GetValues<ScreenshotFormat>())
            {
                string target=Path.Combine(directory,"all-tools."+ImageExportService.Extension(format)); await ScreenshotProjects.SaveAsync(project,rendered,target,format,90,true); var loaded=await ScreenshotProjects.LoadAsync(target);
                UpgradeTests.Ensure(loaded.Annotations.Length==7 && loaded.NextStep==8 && loaded.Crop==surface.CropBounds && loaded.Format==format,"Project attributes lost");
                var restored=new AnnotationSurface(loaded.Original,loaded.Crop);restored.Restore(loaded.Annotations);
                UpgradeTests.Ensure(UpgradeTests.Bytes(restored.Export()).SequenceEqual(UpgradeTests.Bytes(rendered)),"Restored drawing differs from original");
                var actual=ImageExportService.Decode(File.ReadAllBytes(target),format); var expected=ImageExportService.Decode(ImageExportService.Encode(rendered,format,90),format);
                UpgradeTests.Ensure(UpgradeTests.Bytes(actual).SequenceEqual(UpgradeTests.Bytes(expected)),"Export image format/render differs");
            }
        });
        await check("Reopened screenshot selects/moves/styles/deletes with undo/redo, updates thumbnail, and saves independent versions",async()=>
        {
            var loaded=await ScreenshotProjects.LoadAsync(png);var window=new EditorWindow(loaded.Original,new Settings{OutputDirectory=directory},_=>{},loaded);window.Show();
            try
            {
                var s=window.Editor.Surface; var before=s.Export();
                UpgradeTests.Ensure(s.SelectAt(new(280-12,60-10)) && s.Selected?.Tool==AnnotationTool.Step,"Restored step not selectable");
                s.SetSelected(AnnotationGeometry.Move(s.Selected!,new Vector(15,10)) with {StepNumber=12});s.DeleteSelected();s.Undo();s.Undo();UpgradeTests.Ensure(UpgradeTests.Bytes(s.Export()).SequenceEqual(UpgradeTests.Bytes(before)),"Undo restored objects incorrectly");s.Redo();s.Redo();s.Undo();
                s.SetCrop(new(8,8,430,270));s.Undo();s.Redo();var thumbnailBefore=await RecentThumbnailCache.LoadAsync(png,96,"",CancellationToken.None);
                string saved=window.Editor.SaveImage();UpgradeTests.Ensure(saved==png && (await ScreenshotProjects.LoadAsync(png)).Crop==s.CropBounds,"Re-save did not update linked project");
                var thumbnailAfter=await RecentThumbnailCache.LoadAsync(png,96,"",CancellationToken.None);UpgradeTests.Ensure(thumbnailBefore.Image!=null && thumbnailAfter.Image!=null && !UpgradeTests.Bytes(thumbnailBefore.Image).SequenceEqual(UpgradeTests.Bytes(thumbnailAfter.Image)),"Recent thumbnail remained stale");
                string alternate=Path.Combine(directory,"independent.png");await ScreenshotProjects.SaveAsync(ScreenshotProjects.Snapshot(s,13,ScreenshotFormat.Png,90),s.Export(),alternate,ScreenshotFormat.Png,90,true);
                UpgradeTests.Ensure(ScreenshotProjects.LinkFor(alternate)!=ScreenshotProjects.LinkFor(png),"Save-as links shared");
                ScreenshotProjects.Clean(alternate);UpgradeTests.Ensure(File.Exists(alternate) && ScreenshotProjects.HasProject(png) && !ScreenshotProjects.HasProject(alternate),"Project cleanup deleted image or another version");
                foreach(string theme in new[]{"Light","Dark"}) {ThemeService.Apply(theme);window.Editor.SelectTool(AnnotationTool.Select);window.UpdateLayout();UiChangeTests.Render(window,"screenshot-project-"+theme.ToLowerInvariant()+".png");}
            }
            finally{window.Close();}
            // Leave the full seven-object fixture for the separate-process restart check.
            await ScreenshotProjects.SaveAsync(project,rendered,png,ScreenshotFormat.Png,90,true);
        });
        await check("Actual screenshot recent-menu reedit, save, save-as dialog and independent image/project updates",async()=>
        {
            var settings=new Settings {OutputDirectory=directory};settings.Save();var main=new MainWindow();main.Show();
            try
            {
                var list=(ListBox)main.FindName("RecentList");var item=list.Items.OfType<RecentItem>().Single(x=>x.Path==png);list.ScrollIntoView(item);main.UpdateLayout();
                var row=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);row.ContextMenu!.IsOpen=true;await Task.Delay(50);
                var edit=row.ContextMenu.Items.OfType<MenuItem>().Single(x=>x.Header as string=="继续编辑");edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));row.ContextMenu.IsOpen=false;
                EditorWindow? editor=null;for(int n=0;n<100&&editor==null;n++){await Task.Delay(30);editor=Application.Current.Windows.OfType<EditorWindow>().FirstOrDefault(w=>w.IsVisible);}
                UpgradeTests.Ensure(editor!=null && editor.Editor.Surface.Items.Count==7,"Recent menu didn't restore the project");
                var active=editor!;active.Editor.Surface.SetCrop(new(8,8,430,270));active.Editor.SaveAndComplete();while(active.Editor.Saving)await Task.Delay(20);
                UpgradeTests.Ensure((await ScreenshotProjects.LoadAsync(png)).Crop==new Int32Rect(8,8,430,270),"Actual save failed to update crop");
                string name="ui-save-as-"+Guid.NewGuid().ToString("N")+".jpg";var done=new TaskCompletionSource();
                _=Application.Current.Dispatcher.BeginInvoke(new Action(async()=>
                {
                    ScreenshotSaveWindow? dialog=null;
                    try
                    {
                        await Task.Delay(100);dialog=Application.Current.Windows.OfType<ScreenshotSaveWindow>().Single(w=>w.IsVisible);
                        UpgradeTests.Find<ComboBox>(dialog,b=>b.Name=="ImageFormat").SelectedIndex=(int)ScreenshotFormat.Jpg;
                        UpgradeTests.Find<Slider>(dialog,b=>b.Name=="ImageQuality").Value=72;
                        UpgradeTests.Find<TextBox>(dialog,b=>b.Name=="ImageFilename").Text=name;
                        UpgradeTests.Find<Button>(dialog,b=>b.Content as string=="保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        while(dialog.IsVisible)await Task.Delay(20);done.SetResult();
                    }
                    catch(Exception ex){done.SetException(ex);}
                    finally{if(dialog?.IsVisible==true)dialog.Close();}
                }));
                active.Editor.SaveAs();await done.Task;string target=Path.Combine(directory,name);var saved=await ScreenshotProjects.LoadAsync(target);
                UpgradeTests.Ensure(saved.Format==ScreenshotFormat.Jpg&&saved.Quality==72&&saved.Annotations.Length==7&&File.Exists(png),"Save-as failed to preserve both versions");
                active.Editor.Surface.SetCrop(new(12,10,420,260));active.Editor.SaveAndComplete();while(active.Editor.Saving)await Task.Delay(20);
                UpgradeTests.Ensure((await ScreenshotProjects.LoadAsync(target)).Crop==new Int32Rect(12,10,420,260)&&(await ScreenshotProjects.LoadAsync(png)).Crop==new Int32Rect(8,8,430,270),"Save-as versions share mutable state");
                active.Close();UiChangeTests.Render(main,"editable-main-dark.png");
                await ScreenshotProjects.SaveAsync(project,rendered,png,ScreenshotFormat.Png,90,true);
            }
            finally{main.Hide();}
        });
        await check("Plain image import, unsupported/corrupt/missing projects, safe failed save and owned-only cleanup",async()=>
        {
            string plain=Path.Combine(directory,"plain.webp");ImageExportService.Save(original,plain,ScreenshotFormat.WebP,90,true);var newProject=await ScreenshotProjects.LoadAsync(plain);UpgradeTests.Ensure(newProject.Annotations.Length==0 && newProject.ExportPath==null,"Plain image isn't a new project");
            byte[] oldImage=File.ReadAllBytes(png),oldLink=File.ReadAllBytes(ScreenshotProjects.LinkFor(png));
            bool rejected=false;try{await ScreenshotProjects.SaveAsync(project,rendered,png,ScreenshotFormat.Png,90);}catch(IOException){rejected=true;}
            UpgradeTests.Ensure(rejected && File.ReadAllBytes(png).SequenceEqual(oldImage) && File.ReadAllBytes(ScreenshotProjects.LinkFor(png)).SequenceEqual(oldLink),"Failed save changed previous data");
            string link=ScreenshotProjects.LinkFor(png);File.WriteAllText(link,"{broken");rejected=false;try{await ScreenshotProjects.LoadAsync(png);}catch(System.Text.Json.JsonException){rejected=true;}UpgradeTests.Ensure(rejected && File.Exists(png),"Corrupt link accepted/deleted");File.WriteAllBytes(link,oldLink);
            using var doc=System.Text.Json.JsonDocument.Parse(oldLink);string id=doc.RootElement.GetProperty("ProjectId").GetString()!;string file=Path.Combine(ScreenshotProjects.DirectoryPath,id+".qcap");byte[] oldProject=File.ReadAllBytes(file);
            using(var zip=ZipFile.Open(file,ZipArchiveMode.Update)){var entry=zip.GetEntry("project.json")!;string json;using(var reader=new StreamReader(entry.Open()))json=reader.ReadToEnd();entry.Delete();using var writer=new StreamWriter(zip.CreateEntry("project.json").Open());writer.Write(json.Replace("\"Version\":1","\"Version\":99"));}
            rejected=false;try{await ScreenshotProjects.LoadAsync(png);}catch(InvalidDataException){rejected=true;}UpgradeTests.Ensure(rejected && File.Exists(file) && File.ReadAllBytes(png).SequenceEqual(oldImage),"Unsupported version modified existing files");File.WriteAllBytes(file,oldProject);
            File.Move(file,file+".held");rejected=false;try{await ScreenshotProjects.LoadAsync(png);}catch(FileNotFoundException){rejected=true;}UpgradeTests.Ensure(rejected && File.Exists(png),"Missing associated project not reported");File.Move(file+".held",file);
            using(var locked=new FileStream(link,FileMode.Open,FileAccess.Read,FileShare.Read)) { rejected=false;try{await ScreenshotProjects.SaveAsync(project,rendered,png,ScreenshotFormat.Png,90,true);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){rejected=true;} }
            UpgradeTests.Ensure(rejected && File.ReadAllBytes(png).SequenceEqual(oldImage) && File.ReadAllBytes(link).SequenceEqual(oldLink),"Project commit failure did not roll back image");
            UpgradeTests.Ensure(!Directory.GetFiles(directory,"*.partial").Any() && !Directory.GetFiles(directory,"*.backup").Any(),"Save failure left partial files");
        });
    }
}
