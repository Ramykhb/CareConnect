using System.ComponentModel.DataAnnotations;
using BabySitting.Models;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

// #10: babysitter qualifications are collected in settings.
public sealed class QualificationsTests
{
    [Fact]
    public void Settings_form_accepts_qualifications_and_limits_their_length()
    {
        SitterSettingsForm Form(string? q) => new() { FirstName = "A", LastName = "B", City = "Beirut", Bio = "Bio", Currency = "USD", Services = [Enum.GetValues<ServiceType>()[0]], Qualifications = q, FirstAid = true };
        var ok = Form("Paediatric first aid, 2024");
        Assert.True(Validator.TryValidateObject(ok, new ValidationContext(ok), new List<ValidationResult>(), true));
        var tooLong = Form(new string('x', 2001));
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(tooLong, new ValidationContext(tooLong), results, true));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SitterSettingsForm.Qualifications)));
    }
}
