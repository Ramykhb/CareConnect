using System.ComponentModel.DataAnnotations;

namespace PetSitting.ViewModels;

public sealed class SupportRequestForm
{
    [Required(ErrorMessage = "Add a short subject."), StringLength(120)]
    public string Subject { get; set; } = string.Empty;

    [Required(ErrorMessage = "Tell us how we can help."), StringLength(1500, MinimumLength = 10, ErrorMessage = "Write between 10 and 1,500 characters.")]
    public string Message { get; set; } = string.Empty;
}
