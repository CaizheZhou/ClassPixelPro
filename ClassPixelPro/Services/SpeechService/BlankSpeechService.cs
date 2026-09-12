using ClassPixelPro.Core.Abstractions.Services.SpeechService;
using ClassPixelPro.Shared.Abstraction.Services;

namespace ClassPixelPro.Services.SpeechService;

public class BlankSpeechService : ISpeechService
{
    public void EnqueueSpeechQueue(string text)
    {
    }

    public void ClearSpeechQueue()
    {
    }
}