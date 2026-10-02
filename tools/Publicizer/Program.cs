// Makes every type, field, method and nested type of an assembly public, for compiling a plugin
// against a game's internals (the plugin declares IgnoresAccessChecksTo for the runtime).
// Usage: Publicizer <input.dll> <output.dll> [<reference dir>]
using System;
using System.IO;
using Mono.Cecil;

internal static class Program
{
	private static int Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.Error.WriteLine("usage: Publicizer <input.dll> <output.dll> [<reference dir>]");
			return 2;
		}
		var resolver = new DefaultAssemblyResolver();
		resolver.AddSearchDirectory(args.Length > 2 ? args[2] : Path.GetDirectoryName(Path.GetFullPath(args[0])));
		var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
		int count = 0;
		foreach (var module in asm.Modules)
		{
			foreach (var type in module.GetTypes())
			{
				if (type.IsNested)
				{
					type.IsNestedPublic = true;
				}
				else
				{
					type.IsPublic = true;
				}
				foreach (var f in type.Fields)
				{
					// An event's backing field would clash with the event once both are public.
					bool isEventBacking = false;
					foreach (var e in type.Events)
					{
						if (e.Name == f.Name)
						{
							isEventBacking = true;
						}
					}
					if (!isEventBacking)
					{
						f.IsPublic = true;
					}
					count++;
				}
				foreach (var m in type.Methods)
				{
					m.IsPublic = true;
					count++;
				}
			}
		}
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
		asm.Write(args[1]);
		Console.WriteLine($"publicized {count} members -> {args[1]}");
		return 0;
	}
}
