namespace SinmaiAssist.Types;

/// <summary>待转移的单条成绩。</summary>
public class TransferItem
{
    public int musicId;
    public int scoreType;
    public int difficulty;
    public decimal targetAchievement;

    // 状态：0=AP+ 1=AP 2=FC+ 3=FC；-1=未指定（按可达判定自由生成）
    public int comboStatus = -1;

    // 运行时填充
    public string name;
    public uint originalAchievement;
    public int batchIndex;
    public string status = "Pending";
    public string error;

    public void MarkRunning()
    {
        status = "Running";
        error = null;
    }

    public void MarkDone()
    {
        status = "Done";
        error = null;
    }

    public void MarkFailed(string reason)
    {
        status = "Failed";
        error = reason;
    }

    public void MarkSkipped(string reason)
    {
        status = "Skipped";
        error = reason;
    }

    public bool IsFinished => status == "Done" || status == "Failed" || status == "Skipped";
}
