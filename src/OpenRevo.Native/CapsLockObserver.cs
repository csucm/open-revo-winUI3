using OpenRevo.Core;

namespace OpenRevo.Native;

public sealed partial class MainWindow
{
    private CapsLockObserver? capsLockObserver;
    private void StartCapsLockMonitor()
    {
        if(uiReviewMode||capsLockObserver!=null)return;
        capsLockObserver=new(locked=>DispatcherQueue.TryEnqueue(()=>
        {
            if(closed||exitRequested||!osdEnabled)return;
            PresentOsd(OsdMessage.FromFeature("caps_lock",locked?"1":"0"));
        }));
        if(!capsLockObserver.Installed)actions.Add("Caps提示未启动："+capsLockObserver.Failure);
    }
}
