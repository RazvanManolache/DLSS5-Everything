namespace Dlss5CompatApp;

sealed record InstallFileFailure(string RelativePath, string Reason);

sealed record InstallResult(IReadOnlyList<InstallFileFailure> Failures)
{
    public bool Complete => Failures.Count == 0;
    public string Warning => "Installation is incomplete. Other files were still installed.\n\n" +
        string.Join(Environment.NewLine, Failures.Select(file => file.RelativePath + ": " + file.Reason)) +
        "\n\nA file may be blocked by antivirus, in use, or inaccessible. Check your antivirus history and file permissions, then install again. Antivirus protection has not been changed.";

    public void RequireComplete()
    {
        if (!Complete) throw new InvalidOperationException(Warning);
    }
}
