using System.Globalization;
using System.Reflection;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Views;

internal sealed class AboutViewModel
{
    internal AboutViewModel()
    {
        var assembly = typeof(AboutViewModel).Assembly;
        ProductName = assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
        Version = ApplicationVersion.Current;
        CopyrightNotice = string.Create(CultureInfo.InvariantCulture, $"Copyright © {DateTime.Now.Year} yosymph.org.");
    }

    /// <summary>程序集声明的应用名称。</summary>
    public string ProductName { get; }

    /// <summary>发布时写入程序集的产品版本。</summary>
    public string Version { get; }

    /// <summary>使用当前年份的版权声明。</summary>
    public string CopyrightNotice { get; }
}
