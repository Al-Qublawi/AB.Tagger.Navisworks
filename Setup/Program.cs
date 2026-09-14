using System;
using ABAdvTools.Setup;

namespace NwTaggerSetup
{
    internal static class Program
    {
        /// <summary>
        /// See SetupArguments for the command line. The switches this installer always had keep
        /// working: /silent, /silent /allusers, /uninstall.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            return SetupProgram.Run(new TaggerSetup(), args);
        }
    }
}
