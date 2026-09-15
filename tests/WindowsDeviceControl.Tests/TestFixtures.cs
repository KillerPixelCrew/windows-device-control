using System;
using System.ComponentModel;
using Xunit;

namespace WindowsDeviceControl.Tests;

/// <summary>Builders and assertions shared by the test classes.</summary>
internal static class TestFixtures
{
    /// <summary>Asserts that a decode refused its input with ERROR_INVALID_DATA.</summary>
    internal static void AssertInvalidData(Action action)
        => Assert.Equal(13, Assert.Throws<Win32Exception>(action).NativeErrorCode);

    /// <summary>A monitor identity matched by device path, as display discovery reports one.</summary>
    internal static DisplayTargetIdentity Target(string path, string name = "Monitor", uint id = 1) =>
        new(path, null, null, name, 0, 0, id);
}
