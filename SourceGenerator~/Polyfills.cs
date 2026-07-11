// Polyfills required to use C# 9+ records on the netstandard2.0 target frameworks
// (which Roslyn analyzers must target). The compiler generates init-only setters that
// reference this type; the BCL only ships it from .NET 5 onward.

namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
