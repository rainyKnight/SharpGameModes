namespace SharpGameModes.Contracts;

public interface IModeContext
{
    public const string Identity = "SharpGameModes.Contracts.IModeContext";

    ModeContextSnapshot? Current { get; }

    ModeContextSnapshot Activate(MapSelection selection, string source);

    IDisposable Subscribe(Action<ModeContextSnapshot> listener);
}
