using System;
using ClassPixelPro.Shared.IPC.Abstractions.Services;

namespace ClassPixelPro.Services;

public class FooService : IFooService
{
    public void DoSomething()
    {
        Console.WriteLine("Foobar");
    }
}