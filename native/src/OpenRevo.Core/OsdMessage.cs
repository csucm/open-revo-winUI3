namespace OpenRevo.Core;

public sealed record OsdMessage(string Text,string Icon,string Accent,string? Dot=null,int? Steps=null)
{
    public static OsdMessage FromFeature(string feature,string value)
    {
        bool on=value=="1";
        const string blue="#38BDF8",green="#10B981",red="#F43F5E",neutral="#94A3B8",purple="#6366F1";
        if(feature=="power_mode")return value switch
        {"1"=>new("办公模式","office",green),"2"=>new("均衡模式","balance","#3B82F6"),"3"=>new("狂暴模式","beast",red),"4"=>new("极客调校","geek",purple),_=>new("性能模式待读取","balance",neutral)};
        return feature switch
        {
            "fan_boost"=>new("一键强冷 "+(on?"开启":"关闭"),"fan",on?"#06B6D4":neutral,on?"on":"neutral"),
            "wifi"=>new("Wi-Fi 无线网 "+(on?"开启":"关闭"),"wifi",on?blue:red,on?"on":"off"),
            "bluetooth"=>new("蓝牙 "+(on?"开启":"关闭"),"bluetooth",on?blue:red,on?"on":"off"),
            "touchpad"=>new("触控板 "+(on?"开启":"禁用"),"touchpad",on?blue:red,on?"on":"off"),
            "camera"=>new("摄像头 "+(on?"开启":"关闭"),"camera",on?green:red,on?"on":"off"),
            "mic"=>new("麦克风 "+(on?"开启":"静音"),"mic",on?green:red,on?"on":"off"),
            "win_key"=>new("Win键 "+(on?"锁定":"解锁"),"lock",on?"#EFA6C5":neutral,on?"off":"on"),
            "fn_lock"=>new("Fn键 "+(on?"锁定":"解锁"),"fn",on?"#B7A9FF":neutral,on?"on":"neutral"),
            "caps_lock"=>new(on?"Caps已锁定":"Caps已解锁","caps",on?blue:neutral,on?"on":"neutral"),
            "keep_awake"=>new("防休眠 "+(on?"开启":"关闭"),"awake",on?blue:neutral,on?"on":"neutral"),
            "keyboard_brightness" when int.TryParse(value,out int level)&&level is >=0 and <=4=>new(level==0?"关闭背光":"背光开启","keyboard",blue,Steps:level),
            _=>new(feature+" "+value,"balance",blue)
        };
    }
}
