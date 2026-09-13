using System.Collections.Generic;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using Xunit;

namespace JellyfinHuePlugin.Tests.Configuration
{
    public class BridgeChangesTests
    {
        private static HueBridge Bridge(string id, string ip = "192.168.1.50", string key = "key", string name = "Bridge", string hardwareId = "001788fffe123456") =>
            new() { Id = id, Name = name, IpAddress = ip, Username = key, HardwareId = hardwareId };

        [Fact]
        public void AddressChange_IsChanged()
        {
            var old = Bridge("a");
            var result = BridgeChanges.Between(new[] { old }, new[] { Bridge("a", ip: "192.168.1.60") });

            result.Changed.Should().ContainSingle().Which.Old.Should().BeSameAs(old);
            result.Removed.Should().BeEmpty();
        }

        [Fact]
        public void KeyChange_IsChanged()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a") }, new[] { Bridge("a", key: "other") });

            result.Changed.Should().ContainSingle();
        }

        [Fact]
        public void CaseAndWhitespaceOnlyAddressDifference_IsNotAChange()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a", ip: "Bridge.Local") }, new[] { Bridge("a", ip: " bridge.local ") });

            result.Changed.Should().BeEmpty();
            result.Removed.Should().BeEmpty();
            result.Unchanged.Should().ContainSingle();
        }

        [Fact]
        public void RenameOnly_IsUnchanged()
        {
            var old = Bridge("a");
            var renamed = Bridge("a", name: "Living room");

            var result = BridgeChanges.Between(new[] { old }, new[] { renamed });

            result.Changed.Should().BeEmpty();
            result.Removed.Should().BeEmpty();
            var pair = result.Unchanged.Should().ContainSingle().Subject;
            pair.Old.Should().BeSameAs(old);
            pair.New.Should().BeSameAs(renamed);
        }

        [Fact]
        public void IdenticalEntry_IsUnchanged()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a") }, new[] { Bridge("a") });

            result.Changed.Should().BeEmpty();
            result.Unchanged.Should().ContainSingle().Which.New.Id.Should().Be("a");
        }

        [Fact]
        public void ChangedAndRemovedEntries_AreNotAlsoUnchanged()
        {
            var result = BridgeChanges.Between(
                new[] { Bridge("a"), Bridge("b"), Bridge("c") },
                new[] { Bridge("a", ip: "192.168.1.60"), Bridge("c") });

            result.Changed.Should().ContainSingle().Which.Old.Id.Should().Be("a");
            result.Removed.Should().ContainSingle().Which.Id.Should().Be("b");
            result.Unchanged.Should().ContainSingle().Which.Old.Id.Should().Be("c");
        }

        [Fact]
        public void AddedBridge_IsInNoList()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a") }, new[] { Bridge("a"), Bridge("b") });

            result.Changed.Should().BeEmpty();
            result.Removed.Should().BeEmpty();
            result.Unchanged.Should().ContainSingle().Which.New.Id.Should().Be("a");
        }

        [Fact]
        public void MissingId_IsRemoved()
        {
            var old = Bridge("a");
            var result = BridgeChanges.Between(new[] { old, Bridge("b") }, new[] { Bridge("b") });

            result.Removed.Should().ContainSingle().Which.Should().BeSameAs(old);
            result.Changed.Should().BeEmpty();
        }

        [Fact]
        public void ChangedAndRemovedTogether_AreBothReported()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a"), Bridge("b") }, new[] { Bridge("a", key: "new") });

            result.Changed.Should().ContainSingle().Which.New.Username.Should().Be("new");
            result.Removed.Should().ContainSingle().Which.Id.Should().Be("b");
        }

        [Fact]
        public void MissingId_IsRemovedAndNotUnchanged()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a") }, new[] { Bridge("b") });

            result.Removed.Should().ContainSingle().Which.Id.Should().Be("a");
            result.Unchanged.Should().BeEmpty();
        }

        [Fact]
        public void EmptyOrNullBefore_IsNone()
        {
            BridgeChanges.Between(new List<HueBridge>(), new[] { Bridge("a") }).Should().BeSameAs(BridgeChanges.Result.None);
            BridgeChanges.Between(null, new[] { Bridge("a") }).Should().BeSameAs(BridgeChanges.Result.None);
        }

        [Fact]
        public void NullAfter_RemovesEverything()
        {
            var result = BridgeChanges.Between(new[] { Bridge("a") }, null);

            result.Removed.Should().ContainSingle();
        }
    }
}
