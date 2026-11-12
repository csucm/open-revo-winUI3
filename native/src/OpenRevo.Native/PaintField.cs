using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Windows.System;
namespace OpenRevo.Native;

// Numeric/HEX entry needs no TextBox template. It commits once and retains an
// invalid edit for correction; observers never receive a fabricated zero.
internal sealed class PaintField:PaintButton
{
    private readonly TextBlock text=Paint.Text("");private string value="";private bool editing,replace,edited;
    internal event Action<string>? Committed;
    internal Func<bool>? CanCommit { get; set; }
    internal Action<string>? CaptureDraft { get; set; }
    internal string Value{get=>value;set{if(editing)return;this.value=value;text.Text=value;}}
    internal TextAlignment ValueAlignment { get => text.TextAlignment; set => text.TextAlignment = value; }
    internal bool Hex{get;set;}
    internal PaintField(string name,string initial,Action<string>? commit=null):base(name)
    {
        Width=84;Content=text;StretchContent();text.TextAlignment=TextAlignment.Right;Value=initial;if(commit!=null)Committed+=commit;
        Click+=()=>{editing=true;replace=true;};GotFocus+=(_,_)=>{editing=true;replace=true;};LostFocus+=(_,_)=>Commit();
        KeyDown+=async(_,e)=>
        {
            if(!IsEnabled)return;bool ctrl=(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
            if(ctrl&&e.Key==VirtualKey.A){replace=true;e.Handled=true;return;}
            if(ctrl&&e.Key==VirtualKey.V){var clipboard=Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();if(clipboard.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)){string input=await clipboard.GetTextAsync();Edit(input.Trim());}e.Handled=true;return;}
            string? next=e.Key switch{>=VirtualKey.Number0 and <=VirtualKey.Number9=>((int)e.Key-(int)VirtualKey.Number0).ToString(),>=VirtualKey.NumberPad0 and <=VirtualKey.NumberPad9=>((int)e.Key-(int)VirtualKey.NumberPad0).ToString(),VirtualKey.Decimal=>".",VirtualKey.Subtract=>"-",>=VirtualKey.A and <=VirtualKey.F when Hex=>e.Key.ToString(),_=>null};
            if(next!=null){Edit(next);e.Handled=true;}else if(e.Key==VirtualKey.Back){value=replace?"":value.Length>0?value[..^1]:"";replace=false;editing=edited=true;text.Text=value;e.Handled=true;}else if(e.Key==VirtualKey.Escape){editing=edited=false;e.Handled=true;}else if(e.Key==VirtualKey.Enter){Commit();e.Handled=true;}
        };
    }
    private void Edit(string input){if(input.All(c=>char.IsDigit(c)||c is '.' or '-'||Hex&&(c is >= 'a' and <= 'f' or >= 'A' and <= 'F' or '#'))){value=(replace?"":value)+input;replace=false;editing=edited=true;text.Text=value;}}
    private void Commit(){if(!editing||CanCommit?.Invoke()==false)return;editing=edited=false;Committed?.Invoke(value);}

    internal void SaveDraftOnHide()
    {
        if (editing && edited) CaptureDraft?.Invoke(value);
    }
}
