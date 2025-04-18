using System;

namespace TruxScrapper;

public static class AppLogger
{
	public static void Info(string source, string target, string message)
	{
		Write(source, target, message, ConsoleColor.Cyan, "INFO");
	}

	public static void Success(string source, string target, string message)
	{
		Write(source, target, message, ConsoleColor.Green, "OK");
	}

	public static void Warn(string source, string target, string message)
	{
		Write(source, target, message, ConsoleColor.Yellow, "WARN");
	}

	public static void Error(string source, string target, string message)
	{
		Write(source, target, message, ConsoleColor.Red, "ERR");
	}

	private static void Write(string source, string target, string message, ConsoleColor color, string level)
	{
		Console.ForegroundColor = color;
		Console.Write($"[{DateTime.Now:HH:mm:ss}] [{level}] [{source}] [{target}] ");

		Console.ResetColor();
		Console.WriteLine(message);
	}
	
	public static void Info(string message)
	{
		Write(message, ConsoleColor.Cyan, "INFO");
	}

	public static void Success(string message)
	{
		Write(message, ConsoleColor.Green, "OK");
	}

	public static void Warn(string message)
	{
		Write(message, ConsoleColor.Yellow, "WARN");
	}

	public static void Error(string message)
	{
		Write(message, ConsoleColor.Red, "ERR");
	}

	private static void Write(string message, ConsoleColor color, string level)
	{
		Console.ForegroundColor = color;
		Console.Write($"[{DateTime.Now:HH:mm:ss}] [{level}] ");

		Console.ResetColor();
		Console.WriteLine(message);
	}
}
