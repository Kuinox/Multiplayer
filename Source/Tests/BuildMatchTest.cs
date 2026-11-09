using Multiplayer.Common;

namespace Tests;

public class BuildMatchTest
{
    [TestCase("abc1234", "abc1234", true)]
    [TestCase("abc1234", "ABC1234", true)]
    [TestCase("abc1234", "def5678", false)]
    [TestCase("abc1234", "abc1234f", true)]          // same commit, longer abbreviation in a bigger clone
    [TestCase("abc1234e", "abc1234f", false)]
    [TestCase("abc1234", null, false)]               // pre-autoupdate client sends no hash
    [TestCase("abc1234", "def5678-dirty", true)]     // local dev client: protocol check only
    [TestCase("abc1234-dirty", "def5678", true)]     // local dev host: protocol check only
    [TestCase(null, "def5678", true)]
    public void StrictOnlyBetweenReleaseBuilds(string? server, string? client, bool expected) =>
        Assert.That(ServerJoiningState.BuildMatches(server, client), Is.EqualTo(expected));

    [TestCase("0.11.5+abc1234", "abc1234")]
    [TestCase("0.11.5+abc1234-dirty", "abc1234-dirty")]
    [TestCase("0.11.5", null)]
    public void HashFromVersion(string version, string? expected) =>
        Assert.That(MpVersion.HashFromVersion(version), Is.EqualTo(expected));
}
