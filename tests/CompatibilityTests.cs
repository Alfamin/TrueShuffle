using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrueShuffle;

internal static class CompatibilityTests
{
    public static void Run(Action<bool,string> check)
    {
        var folder=Path.Combine(Path.GetTempPath(),"shuffle-identity-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string original=Path.Combine(folder,"original.flac"), moved=Path.Combine(folder,"renamed.flac");
        File.WriteAllText(original,"synthetic recording");
        Song SongAt(string path)=>new("id",path,"Example","Artist",0,false,false,false,null);
        var old=SongAt(original);
        var legacy=old.LegacyKey;
        var upgraded=ShuffleEngine.UpgradeHistory(new[]{old},new[]{legacy},legacy);
        check(upgraded.Seen.Contains(old.Key) && upgraded.Last==old.Key,"Old path history migrates to stable file identity");
        string identity=old.Key;
        File.Move(original,moved);
        var renamed=SongAt(moved);
        check(!OperatingSystem.IsWindows() || identity==renamed.Key,"Same-drive rename keeps no-repeat identity");
        check(legacy!=renamed.LegacyKey,"Legacy path key actually changes during rename");
        var another=Path.Combine(folder,"another.flac");File.Copy(moved,another);
        check(!OperatingSystem.IsWindows() || SongAt(another).Key!=renamed.Key,"Independent duplicate files keep distinct identities");
        var plan=ShuffleEngine.Build(new[]{renamed,SongAt(another)},ShuffleMode.NoRepeats,upgraded.Seen,null,upgraded.Last);
        check(!OperatingSystem.IsWindows() || plan.Songs.Count==1 && plan.Songs[0].Path==another,"Renamed heard song stays excluded while a new copy remains eligible");
        var pool=new[]{renamed,SongAt(another)};
        var exhausted=pool.SelectMany(s=>new[]{s.Key,s.LegacyKey}).ToArray();
        var reset=ShuffleEngine.Build(pool,ShuffleMode.NoRepeats,exhausted,null,renamed.Key);
        var afterReset=ShuffleEngine.UpgradeHistory(pool,reset.Seen,renamed.Key);
        check(reset.NewCycle && !afterReset.Seen.Any(),"Fresh cycle clears legacy aliases so migration cannot resurrect old heard songs");
        var afterTags=ShuffleEngine.UpgradeHistory(pool,new[]{renamed.LegacyKey},"old-file-identity",renamed.LegacyKey);
        check(afterTags.Last==renamed.Key,"Last-song identity survives native atomic tag replacement");
        var native=new FakeShortcutSettings();
        check(ShuffleShortcut.Gesture("Follow Noctis",native)?.Key==Key.S,"Shortcut follows Noctis default");
        native.ShortcutService.Binding=new(Key.J,KeyModifiers.Control|KeyModifiers.Shift);
        check(ShuffleShortcut.Gesture(null,native)?.Equals(native.ShortcutService.Binding)==true,"Shortcut follows live native rebinding");
        native.ShortcutService.Binding=null;
        check(ShuffleShortcut.Gesture(null,native)==null,"Unbound native shuffle remains unbound");
        check(ShuffleShortcut.Gesture("off",native)==null,"Explicit off preserves native key handling");
        bool refused=false;try{ShuffleShortcut.Gesture("S",native);}catch{refused=true;}
        check(refused,"A custom bare-letter shortcut cannot steal typing");

        int custom=0, builtIn=0;
        native.ShortcutService.Binding=new(Key.S,KeyModifiers.Control);
        var text=new TextBox();var window=new Window {Content=text};window.Show();Dispatcher.UIThread.RunJobs();
        window.AddHandler(InputElement.KeyDownEvent,(_,e)=>{if(e.Key==Key.S)builtIn++;},RoutingStrategies.Bubble);
        var attachment=new ShuffleShortcut(_=>native,_=>true,()=>"Follow Noctis",_=>custom++,_=>{});
        void Press(Key key,KeyModifiers modifiers,Control? target=null)
        { (target??window).RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=key,KeyModifiers=modifiers});Dispatcher.UIThread.RunJobs(); }
        Press(Key.S,KeyModifiers.Control);
        check(custom==1 && builtIn==0,"Tunnel handler opens custom modes before native shuffle runs");
        Press(Key.S,KeyModifiers.None,text);
        check(custom==1,"Typing in a text box never opens shuffle modes");
        native.Shortcuts.IsRecording=true;Press(Key.S,KeyModifiers.Control);
        check(custom==1 && builtIn==2,"Shortcut recording retains the key");
        native.Shortcuts.IsRecording=false;
        native.ShortcutService.Binding=new(Key.J,KeyModifiers.Control);Press(Key.S,KeyModifiers.Control);
        check(custom==1 && builtIn==3,"Old shortcut is released after native rebinding");
        Press(Key.J,KeyModifiers.Control);check(custom==2,"Rebound shortcut opens modes");
        native.ShortcutService.Binding=new(Key.Left,KeyModifiers.Control);Press(Key.Left,KeyModifiers.Control,text);
        check(custom==2,"Rebound cursor shortcut still leaves text editing intact");
        native.ShortcutService.Binding=new(Key.J,KeyModifiers.Control);
        attachment.Dispose();Press(Key.J,KeyModifiers.Control);
        check(custom==2,"Disabling shortcut detaches event registration");
        window.Close();
    }
}
public enum FakeShortcutAction { ToggleShuffle }
public sealed class FakeShortcutService
{
    public KeyGesture? Binding {get;set;}=new(Key.S,KeyModifiers.Control);
    public KeyGesture? Get(FakeShortcutAction action)=>Binding;
}
public sealed class FakeRecording { public bool IsRecording {get;set;} }
public sealed class FakeShortcutSettings
{
    public FakeShortcutService ShortcutService {get;}=new();
    public FakeRecording Shortcuts {get;}=new();
}
