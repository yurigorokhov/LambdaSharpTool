/*
 * MindTouch λ#
 * Copyright (C) 2006-2018-2019 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Runtime.InteropServices;

namespace LambdaSharp.Tool {

    public class AnsiTerminal : IDisposable {

        //--- Constants ---
        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

        //--- Class Methods ---
        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        public static extern uint GetLastError();

        //--- Fields ---
        private bool _enableAnsiOutput;
        private bool _switchedToAnsi;
        private IntPtr _consoleStandardOut;
        private uint _originaConsoleMode;

        //--- Constructors ---
        public AnsiTerminal(bool enableAnsiOutput) {
            _enableAnsiOutput = enableAnsiOutput;
            if(_enableAnsiOutput && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                SwitchWindowsConsoleToAnsi();
            }
        }

        //--- Methods ---
        public void Dispose() {
            RestoreWindowsConsoleSettings();
        }

        private void SwitchWindowsConsoleToAnsi() {
            _consoleStandardOut = GetStdHandle(STD_OUTPUT_HANDLE);
            _switchedToAnsi = GetConsoleMode(_consoleStandardOut, out _originaConsoleMode)
                && SetConsoleMode(_consoleStandardOut, _originaConsoleMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);
        }

        private void RestoreWindowsConsoleSettings() {
            if(_switchedToAnsi) {
                SetConsoleMode(_consoleStandardOut, _originaConsoleMode);
            }
        }
    }
}
