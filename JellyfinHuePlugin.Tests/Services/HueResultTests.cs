using System;
using FluentAssertions;
using JellyfinHuePlugin.Services;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    public class HueResultTests
    {
        [Fact]
        public void Failure_RejectsAWhitespaceReason()
        {
            Action resultFailure = () => HueResult<string>.Failure("   ");
            Action outcomeFailure = () => HueOutcome.Failure("   ");

            resultFailure.Should().Throw<ArgumentException>();
            outcomeFailure.Should().Throw<ArgumentException>();
        }
    }
}
