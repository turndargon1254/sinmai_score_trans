namespace SinmaiAssist.Types;

public class TransferChartInfo
{
    public bool enable;
    public int level;
    public int levelDecimal;
    public string designer;
}

public class TransferSongInfo
{
    public int id;
    public int scoreType;
    public string name;
    public string artist;
    public TransferChartInfo[] charts;
}
