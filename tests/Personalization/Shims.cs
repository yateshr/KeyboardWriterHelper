// The linked pure sources share imports with the Windows application, but do
// not use WinForms. Suggestions here are deterministic dictionary fixtures;
// Windows' real dictionaries and keyboard hook remain covered by test.cmd.
namespace System.Windows.Forms { }

namespace KeyboardWriterHelper
{
    sealed class Suggestion
    {
        public bool Known;
        public string Fix;
        public string[] Others = new string[0];
    }
}
