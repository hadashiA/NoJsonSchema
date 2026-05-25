#if !NET5_0_OR_GREATER
using System.ComponentModel;

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

[EditorBrowsable(EditorBrowsableState.Never)]
static class IsExternalInit
{
}
#endif
