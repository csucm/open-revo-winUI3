using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;
using OpenRevo.Core;
namespace OpenRevo.Native;
public sealed partial class MainWindow
{
    private Popup? paintDialog;
    private Task<bool> PaintDialog(string title,UIElement content,string primary="",string cancel="完成",int timeoutMs=0)
    {
        if(root.XamlRoot==null)return Task.FromResult(false);
        var done=new TaskCompletionSource<bool>();var shade=new Grid{Width=root.XamlRoot.Size.Width,Height=root.XamlRoot.Size.Height,Background=Paint.Brush("66000000")};var stack=Paint.Stack(16,Paint.Text(title,16,true),content);var buttons=Paint.Inline(8);buttons.HorizontalAlignment=HorizontalAlignment.Right;
        void Finish(bool accepted){if(done.TrySetResult(accepted)){if(paintDialog!=null)paintDialog.IsOpen=false;dialogOpen=false;}}
        var close=new PaintButton(cancel,()=>Finish(false));buttons.Children.Add(close);if(primary.Length>0)buttons.Children.Add(new PaintButton(primary,()=>Finish(true)){Variant="primary"});stack.Children.Add(buttons);var card=Paint.Card(stack,24,16);card.Width=Math.Min(520,root.XamlRoot.Size.Width-40);card.HorizontalAlignment=HorizontalAlignment.Center;card.VerticalAlignment=VerticalAlignment.Center;card.RequestedTheme=root.ActualTheme;shade.Children.Add(card);shade.KeyDown+=(_,e)=>{if(e.Key==VirtualKey.Escape){Finish(false);e.Handled=true;}};
        paintDialog=new Popup{XamlRoot=root.XamlRoot,Child=shade,IsLightDismissEnabled=false};paintDialog.Closed+=(_,_)=>Finish(false);dialogOpen=true;paintDialog.IsOpen=true;close.Focus(FocusState.Programmatic);
        if(timeoutMs>0)_=Expire();async Task Expire(){await Task.Delay(timeoutMs);Finish(false);}return done.Task;
    }
    private async Task<string?> PaintPresetName(string title,string initial)
    {
        var input=new PaintTextEntry(initial);return await PaintDialog(title,input,"保存","取消")?TuningPresetStore.ValidName(input.Text):null;
    }
}
// A bare edit surface supplies Windows IME, selection and clipboard support.
// The surrounding geometry and text presentation have no stock chrome.
internal sealed class PaintTextEntry:ContentControl
{
    private readonly TextBox editor;
    internal string Text=>editor.Text;
    internal PaintTextEntry(string initial)
    {
        editor=(TextBox)Microsoft.UI.Xaml.Markup.XamlReader.Load("<TextBox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontFamily='Segoe UI, Microsoft YaHei' Language='zh-CN' FontSize='14' Padding='0' BorderThickness='0' Background='Transparent' MinHeight='0' MaxLength='12'><TextBox.Template><ControlTemplate TargetType='TextBox'><ScrollViewer x:Name='ContentElement' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' IsTabStop='False'/></ControlTemplate></TextBox.Template></TextBox>");
        editor.Text=initial;var card=Paint.Card(editor,0,8,true);card.Padding=new(9,6,9,6);Content=card;HorizontalContentAlignment=HorizontalAlignment.Stretch;Loaded+=(_,_)=>{editor.Foreground=Paint.Brush(Paint.Ink(this));editor.Focus(FocusState.Programmatic);editor.SelectAll();};
    }
}
