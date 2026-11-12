namespace OpenRevo.Native;

// Pure row packing is shared with headless layout checks. Full rows and trailing
// partial rows consume the entire available width, with no empty grid cells.
internal readonly record struct DesktopGridRow(int Start,int Count,double ItemWidth);
internal static class DesktopGridGeometry
{
    internal static int Columns(double width,int preferred,double minimum,double gap,bool pairFour=false,bool quick=false)
    {
        int columns=Math.Clamp((int)((Math.Max(0,width)+gap)/(minimum+gap)),1,preferred);
        if(pairFour&&preferred is 4 or 8)columns=columns>=8?8:columns>=4?4:columns>=2?2:1;
        if(quick)columns=columns>=preferred?preferred:columns>=6?6:columns>=4?4:columns>=2?2:1;
        return columns;
    }
    internal static IReadOnlyList<DesktopGridRow> Rows(double width,int columns,double gap,IReadOnlyList<bool> fullRows)
    {
        var rows=new List<DesktopGridRow>();
        for(int start=0;start<fullRows.Count;)
        {
            int count=1;
            if(!fullRows[start])while(count<columns&&start+count<fullRows.Count&&!fullRows[start+count])count++;
            rows.Add(new(start,count,Math.Max(0,(width-gap*(count-1))/count)));start+=count;
        }
        return rows;
    }
}
