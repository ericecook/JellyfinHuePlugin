using FluentAssertions;
using JellyfinHuePlugin.Api;
using JellyfinHuePlugin.Configuration;
using JellyfinHuePlugin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyfinHuePlugin.Tests.Api
{
    /// <summary>
    /// Runs MVC's own model validation over the test request, as [ApiController] does before the
    /// action runs. The plugin has nullable reference types on and Jellyfin 12 keeps MVC's implicit
    /// [Required] for non-nullable references; that attribute allows empty strings, so the page's
    /// profile (empty scene, client and IP fields) must stay valid. A missing Action or Profile is not.
    /// </summary>
    public class TestLightRequestValidationTests
    {
        private static bool IsValid(TestLightRequest request)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMvcCore().AddDataAnnotations();
            using var provider = services.BuildServiceProvider();

            var actionContext = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());
            provider.GetRequiredService<IObjectModelValidator>().Validate(actionContext, validationState: null, prefix: string.Empty, model: request);
            return actionContext.ModelState.IsValid;
        }

        [Fact]
        public void AProfileWithEmptyStringsIsValid()
        {
            var profile = new LightControlProfile
            {
                BridgeId = "",
                TargetClientName = "",
                TargetIpAddress = "",
                PlaySceneId = "",
                PauseSceneId = "",
                StopSceneId = ""
            };

            IsValid(new TestLightRequest { Action = LightAction.Play, Profile = profile }).Should().BeTrue();
        }

        [Fact]
        public void AMissingActionIsInvalid()
        {
            IsValid(new TestLightRequest { Profile = new LightControlProfile() }).Should().BeFalse();
        }

        [Fact]
        public void AMissingProfileIsInvalid()
        {
            IsValid(new TestLightRequest { Action = LightAction.Play }).Should().BeFalse();
        }

        [Fact]
        public void AProfileWithANullStringIsInvalid()
        {
            // The implicit [Required] is really applied: it allows "" but not null.
            var profile = new LightControlProfile { PlaySceneId = null! };

            IsValid(new TestLightRequest { Action = LightAction.Play, Profile = profile }).Should().BeFalse();
        }
    }
}
