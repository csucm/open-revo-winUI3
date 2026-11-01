namespace OpenRevo.Core.Hal;

public static class FanCurveEditing
{
    // Original editor behavior: changing a duty pushes/pulls neighboring duties
    // to preserve monotonicity. Temperatures remain the original node anchors.
    public static FanPoint[] WithDuty(FanPoint[] points,int index,int duty)
    {
        if(points.Length!=8)throw new ArgumentException("风扇曲线需要8个节点。",nameof(points));
        if(index is <0 or >7)throw new ArgumentOutOfRangeException(nameof(index));
        var next=points.ToArray();int target=index==7?100:Math.Clamp(duty,0,100);
        next[index]=next[index] with{Percent=target};
        for(int i=0;i<index;i++)if(next[i].Percent>target)next[i]=next[i] with{Percent=target};
        for(int i=index+1;i<7;i++)if(next[i].Percent<target)next[i]=next[i] with{Percent=target};
        next[7]=next[7] with{Percent=100};return next;
    }
}
