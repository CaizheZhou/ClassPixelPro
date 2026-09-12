// See https://aka.ms/new-console-template for more information

using XmlDocMarkdown.Core;

namespace ClassPixelPro.DocsGenerator;

static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("ClassPixelPro Document Generator");
        return XmlDocMarkdownApp.Run(args);
    }
}