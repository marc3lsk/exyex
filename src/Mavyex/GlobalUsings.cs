// WPF projects leave System.IO out of the implicit usings (name clash with System.Windows.Shapes.Path).
// Use System.Windows.Shapes.Path explicitly where a shape is meant.
global using System.IO;
global using Path = System.IO.Path;
