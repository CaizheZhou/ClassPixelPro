using Linearstar.Windows.RawInput;

namespace ClassPixelPro.Models.EventArgs;

public class RawInputEventArgs(RawInputData data) : System.EventArgs
{
    public RawInputData Data { get; } = data;
}