namespace RepoBackup.Tests;

public sealed class TestRunner
{
    private int passed;
    private int failed;
    public async Task Run(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + "\n" + error); }
    }
    public int Finish() { Console.WriteLine($"\n{passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
    public static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
