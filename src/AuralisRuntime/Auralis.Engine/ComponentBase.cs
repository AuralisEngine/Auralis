namespace Auralis.Engine;

/// <summary>Base for higher-level engine objects that need a persistent object identity and name.</summary>
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

    public void Destroy(ComponentBase component) => component?.OnDestroy();
}
