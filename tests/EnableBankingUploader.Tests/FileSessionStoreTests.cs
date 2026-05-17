using EnableBankingUploader.Core.Options;
using EnableBankingUploader.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnableBankingUploader.Tests;

[TestClass]
public class FileSessionStoreTests
{
    private static StoredSession SampleSession(string id = "abc123") =>
        new(id, "TestBank", "FI", ["uid-1", "uid-2"],
            new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static FileSessionStore CreateStore(string dir) =>
        new(Options.Create(new SyncOptions
        {
            EnableBankingApplicationId = "x",
            EnableBankingPrivateKeyPath = "/x",
            FireflyIiiUrl = "http://localhost",
            FireflyIiiToken = "t",
            SessionStorePath = dir,
        }), NullLogger<FileSessionStore>.Instance);

    [TestMethod]
    public async Task ListAsync_EmptyDirectory_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = CreateStore(dir);
            var result = await store.ListAsync();
            Assert.IsEmpty(result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveAndList_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = CreateStore(dir);
            var session = SampleSession("session-abc");
            await store.SaveAsync(session);

            var result = await store.ListAsync();
            Assert.HasCount(1, result);
            Assert.AreEqual("session-abc", result[0].SessionId);
            Assert.AreEqual("TestBank", result[0].AspspName);
            Assert.AreEqual("FI", result[0].AspspCountry);
            Assert.HasCount(2, result[0].AccountUids);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveAsync_OverwritesExisting()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = CreateStore(dir);
            await store.SaveAsync(SampleSession("s1"));
            var updated = SampleSession("s1") with { AspspName = "OtherBank" };
            await store.SaveAsync(updated);

            var result = await store.ListAsync();
            Assert.HasCount(1, result);
            Assert.AreEqual("OtherBank", result[0].AspspName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = CreateStore(dir);
            await store.SaveAsync(SampleSession("del-me"));
            await store.DeleteAsync("del-me");

            var result = await store.ListAsync();
            Assert.IsEmpty(result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var store = CreateStore(dir);
            await store.DeleteAsync("no-such-session");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListAsync_MalformedFile_IsSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), "this is not json {{");
            var store = CreateStore(dir);
            await store.SaveAsync(SampleSession("good"));

            var result = await store.ListAsync();
            Assert.HasCount(1, result);
            Assert.AreEqual("good", result[0].SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void GetPath_InvalidSessionId_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = CreateStore(dir);
        // GetPath validates synchronously before returning a Task, so the exception propagates directly
        Assert.ThrowsExactly<ArgumentException>(() => store.GetAsync("../escape"));
    }
}
