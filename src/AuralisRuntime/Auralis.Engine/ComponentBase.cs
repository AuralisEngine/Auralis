namespace Auralis.Engine;

/// <summary>
/// Base for engine objects that need persistent identity, a name, and lifecycle hooks.
/// </summary>
public abstract class ComponentBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = string.Empty;

    public virtual string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            OnNameChanged();
        }
    }

    protected virtual void OnNameChanged() { }
    protected virtual void OnDestroy() { }
}
