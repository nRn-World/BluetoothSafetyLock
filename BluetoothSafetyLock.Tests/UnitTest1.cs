using Xunit;
using BluetoothSafetyLock;
using System;

namespace BluetoothSafetyLock.Tests
{
    public class AppSettingsAndUpdaterTests
    {
        [Fact]
        public void AppSettings_Defaults_AreValid()
        {
            var settings = new AppSettings();
            Assert.Equal((short)-100, settings.Threshold);
            Assert.Equal(15, settings.GracePeriodSeconds);
            Assert.True(settings.IsLockWorkstationEnabled);
            Assert.True(settings.IsAutoUnlockEnabled);
            Assert.True(settings.IsClearClipboardEnabled);
            Assert.False(settings.IsPlayWarningEnabled);
            Assert.Equal("Auto", settings.AppearanceTheme);
            Assert.True(settings.IsAutoUpdateEnabled);
        }

        [Theory]
        [InlineData("v1.2.3", "1.2.3.0")]
        [InlineData("1.1.4", "1.1.4.0")]
        [InlineData("v2.0.0-beta.1", "2.0.0.0")]
        public void TryParseVersionTag_ParsesAndNormalizesVersion(string tag, string expectedVersion)
        {
            bool success = UpdaterService.TryParseVersionTag(tag, out Version version);
            Assert.True(success);
            Assert.Equal(new Version(expectedVersion), version);
        }

        [Fact]
        public void NormalizeVersion_EnsuresFourComponents()
        {
            var v2 = new Version(1, 2);
            var normalized = UpdaterService.NormalizeVersion(v2);
            Assert.Equal(1, normalized.Major);
            Assert.Equal(2, normalized.Minor);
            Assert.Equal(0, normalized.Build);
            Assert.Equal(0, normalized.Revision);
        }
    }

    public class MonitoringServiceTests
    {
        [Fact]
        public void MonitoringService_Properties_GetAndSetCorrectly()
        {
            using var mgr = new BluetoothManager();
            using var svc = new MonitoringService(mgr);

            svc.Threshold = -70;
            svc.GracePeriodSeconds = 10;
            svc.MonitoredDeviceName = "MyPhone";

            Assert.Equal((short)-70, svc.Threshold);
            Assert.Equal(10, svc.GracePeriodSeconds);
            Assert.Equal("MyPhone", svc.MonitoredDeviceName);
            Assert.True(svc.IsPaused);
            Assert.False(svc.IsLocked);
        }
    }
}
