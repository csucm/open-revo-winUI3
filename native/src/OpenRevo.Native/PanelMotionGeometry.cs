namespace OpenRevo.Native;
internal readonly record struct PanelMotionRect(int X,int Y,int Width,int Height);
internal static class PanelMotionGeometry
{
    internal const int DurationMilliseconds=280;
    internal static double Ease(double progress)
    {
        double t=double.IsFinite(progress)?Math.Clamp(progress,0,1):1;
        if(t is 0 or 1)return t;
        // The same cubic-bezier(.2,0,.2,1) as the compositor; invert x(t).
        double low=0,high=1;
        for(int i=0;i<20;i++)
        {
            double u=(low+high)/2,one=1-u;
            double x=.6*one*one*u+.6*one*u*u+u*u*u;
            if(x<t)low=u;else high=u;
        }
        double s=(low+high)/2;return 3*(1-s)*s*s+s*s*s;
    }
    internal static PanelMotionRect At(PanelMotionRect start,PanelMotionRect end,double progress)
    {
        double t=Ease(progress);
        int Mix(int a,int b)=>(int)Math.Round(a+((double)b-a)*t);
        return new(Mix(start.X,end.X),Mix(start.Y,end.Y),Math.Max(1,Mix(start.Width,end.Width)),Math.Max(1,Mix(start.Height,end.Height)));
    }
}
