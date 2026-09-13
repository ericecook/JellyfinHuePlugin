using System;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
using Xunit;

namespace JellyfinHuePlugin.Tests.Configuration
{
    public class HueConfigurationStoreTests
    {
        [Fact]
        public void Unattached_CurrentThrowsANamedError()
        {
            var store = new HueConfigurationStore();

            var act = () => store.Current;

            act.Should().Throw<InvalidOperationException>().WithMessage("The Hue plugin has not been loaded yet");
        }

        [Fact]
        public void Unattached_SaveThrows()
        {
            var store = new HueConfigurationStore();

            var act = () => store.Save();

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Attach_Null_Throws()
        {
            var store = new HueConfigurationStore();

            var act = () => store.Attach(null!);

            act.Should().Throw<ArgumentNullException>();
        }
    }
}
