namespace Tedd.Defrag.Desktop;

/// <summary>Whole-number editing with hard upper bounds and inline feedback for incomplete values.</summary>
public sealed class ConstrainedEntry : Entry
{
    private InputBounds? bounds;
    private bool updating;
    private string? externalError;
    private readonly Label feedback = new() { FontSize = 10, IsVisible = false };
    public string FieldName { get; set; } = "Value";
    public bool IsInputValid => bounds == null || bounds.IsValid(Text) && externalError == null;
    public bool HasValidNumber => bounds?.IsValid(Text) != false;
    public string? Error => externalError ?? (bounds?.IsValid(Text) == false ? bounds.RangeDescription : null);
    public ulong Value => bounds != null && bounds.TryRead(Text, out ulong value) ? value : 0;
    public event EventHandler? ValidationChanged;

    public ConstrainedEntry()
    {
        TextChanged += OnTextChanged;
        Unfocused += (_, _) => Commit();
        Completed += (_, _) => Commit();
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsEnabled)) ShowFeedback(); };
        feedback.SetDynamicResource(Label.TextColorProperty, "DangerText");
    }

    internal void Configure(InputBounds constraint, bool normalize = true)
    {
        if (bounds == constraint) return;
        bounds = constraint;
        Keyboard = constraint.Hexadecimal ? Keyboard.Text : Keyboard.Numeric;
        SemanticProperties.SetHint(this, constraint.RangeDescription);
        if (Parent is VerticalStackLayout parent && !parent.Children.Contains(feedback))
            parent.Children.Insert(parent.Children.IndexOf(this) + 1, feedback);
        if (normalize) Commit(); else PublishValidation();
    }

    public void SetExternalError(string? message)
    {
        if (externalError == message) return;
        externalError = message;
        ShowFeedback();
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (updating || bounds == null) return;
        string? warning = null;
        string? replacement = null;
        if (!IsFocused) replacement = bounds.Normalize(e.NewTextValue);
        else if (!bounds.CanEdit(e.NewTextValue))
        {
            replacement = e.OldTextValue ?? "";
            warning = bounds.RangeDescription;
        }
        if (replacement != null && replacement != Text)
        {
            updating = true;
            try { Text = replacement; CursorPosition = replacement.Length; }
            finally { updating = false; }
        }
        PublishValidation(warning);
    }

    public void Commit()
    {
        if (bounds == null) return;
        string normalized = bounds.Normalize(Text);
        if (normalized != Text)
        {
            updating = true;
            try { Text = normalized; }
            finally { updating = false; }
        }
        PublishValidation();
    }

    private void PublishValidation(string? warning = null)
    {
        ShowFeedback(warning);
        ValidationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ShowFeedback(string? warning = null)
    {
        feedback.Text = warning ?? Error;
        feedback.IsVisible = IsEnabled && !string.IsNullOrEmpty(feedback.Text);
    }
}
