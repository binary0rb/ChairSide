using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace ChairSide.Board.Tests;

internal sealed class TestWebHostEnvironment(string contentRootPath, string environmentName) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "ChairSide.Board.Tests";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

    public string ContentRootPath { get; set; } = contentRootPath;

    public string EnvironmentName { get; set; } = environmentName;

    public string WebRootPath { get; set; } = Path.Combine(contentRootPath, "wwwroot");

    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
