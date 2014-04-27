﻿using System;
using System.IO;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

public sealed class QemuEmulator : IEmulator {
    private string p_DiskImage;
    private Process p_Process;
    private Process p_QemuProcess;
    private Socket p_Socket;
    private NetworkStream p_Stream;
    private bool p_Suspended;

    public QemuEmulator(string diskImage) {
        p_DiskImage = diskImage;
    }

    delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    #region State
    public void Start(int memory, StandardOutputCallback standardOutput) {
        //already running?
        if (Running) { return; }

        #region Invoke Qemu
        string qemuArguments = "-qmp tcp:127.0.0.1:4444,server,nowait " +
                               "-cdrom \"" + new FileInfo(p_DiskImage).FullName + "\" " +
                               "-m " + memory;

        //create a processlink process so that if this process is killed
        //it automatically kills the emulator as well.
        standardOutput("Launching process linker...");
        if (!File.Exists("processlink.exe")) {
            standardOutput("  Error! ProcessLink.exe is not found!");
            return;
        }
        p_Process = new Process() {
            StartInfo = new ProcessStartInfo { 
                FileName = "processLink.exe",
                Arguments = "-id " + Process.GetCurrentProcess().Id + " " +
                            "-f \"" + new FileInfo("emulators/qemu/qemu.exe").FullName + "\" " + 
                            "-a " + qemuArguments,
                UseShellExecute = false,
                CreateNoWindow = true,     
                RedirectStandardOutput = true
            }
        };

        //get the qemu process id from the spawn process
        int qemuPID = -1;
        p_Process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
            if (e.Data == null || qemuPID != -1) { return; }            
            qemuPID = Convert.ToInt32(e.Data);
        };

        //launch the process
        standardOutput("Starting Qemu...");
        p_Process.Start();
        standardOutput("   Process linker PID=" + p_Process.Id);
        p_Process.BeginOutputReadLine();

        //wait until the spawner has launched Qemu
        while (qemuPID == -1) ;
        p_QemuProcess = Process.GetProcessById(qemuPID);
        standardOutput("   Started, PID=" + qemuPID);

        #endregion

        //connect to the Qemu Monitor
        p_Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
        int connectStart = Environment.TickCount;
        standardOutput("Connecting to Qemu's QMP service...");
        while (Environment.TickCount < connectStart + 1000) {
            try { 
                p_Socket.Connect("localhost", 4444);
                p_Stream = new NetworkStream(p_Socket);
                break;
            }
            catch { }
        }

        //timed out?
        if (!p_Socket.Connected) {
            standardOutput("Error! Unable to connect to Qemu's QMP service");
            Stop();
            return;
        }
        
        //get the initial response from the server
        standardOutput("Performing handshake with QMP Server");
        JSONObject handshake = JSONObject.Decode("", monitorRead())[0];
        handshake = (JSONObject)handshake["QMP"];

        //get the version of the qemu emulator        
        JSONObject version = (JSONObject)handshake["version"];
        version = (JSONObject)version["qemu"];
        int versionMajor = Convert.ToInt32(version["major"]);
        int versionMinor = Convert.ToInt32(version["minor"]);

        //enter command mode
        monitorWrite("{ \"execute\": \"qmp_capabilities\" }");
        JSONObject result = JSONObject.Decode(monitorRead())[0];
        result = (JSONObject)result["return"];
        if (result.ChildValues[0].ChildCount > 0) {
            standardOutput("Unable to enter into QMP Command mode!");
            Stop();
            return;
        }

        //success
        standardOutput("Qemu Emulator version " + versionMajor + "." + versionMinor);
        standardOutput("Qemu Emulator ready...");


        GetProcessors();
    }
    public bool Running {
        get {
            if (p_QemuProcess == null) { return false; }
            try {
                if (Process.GetProcessById(p_QemuProcess.Id) == null) {
                    return false;
                }
                return true;
            }
            catch { return false; }
        }
    }
    public void Stop() {
        if (p_Process == null) { return; }
        try { p_Socket.Close(); }
        catch { }
        try { p_QemuProcess.Kill(); }
        catch { }
        p_Socket = null;
        p_Stream = null;
        p_QemuProcess = null;
        p_Process = null;
    }

    public bool Suspended { get { return p_Suspended; } }
    public void Suspend() {
        monitorExecuteCmd("stop");
        monitorRead();
        p_Suspended = true;
    }
    public void Resume() {
        monitorExecuteCmd("cont");
        monitorRead();
        p_Suspended = false;
    }
    #endregion

    public EmulationCPU[] GetProcessors() { 
        //get the JSON array which contains all the processors
        //running in the emulation.
        monitorExecute("query-cpus", null);
        JSONObject processors = JSONObject.Decode(monitorRead())[0];
        processors = (JSONObject)processors["return"];
        JSONObject[] children = processors.ChildValues[0].ChildValues;
        
        //read the JSON object into an EmulationCPU object array
        EmulationCPU[] buffer = new EmulationCPU[children.Length];
        for (int c = 0; c < buffer.Length; c++) {
            JSONObject obj = children[c];
            buffer[c] = new EmulationCPU(
                Convert.ToInt32(obj["cpu"]),
                Convert.ToBoolean(obj["current"]),
                Convert.ToInt64(obj["pc"]),
                Convert.ToBoolean(obj["halted"]));


        }
        return buffer;
    }
    public void UpdateProcessor(ref EmulationCPU processor) {
        //get the current state of all processors
        EmulationCPU[] processors = GetProcessors();

        //look for the processor to update in the 
        //list of processors we just got.
        for (int c = 0; c < processors.Length; c++) {
            if (processors[c].Index == processors[c].Index) {
                processor = processors[c];
                break;
            }
        }
    }

    #region Helpers
    private void monitorExecuteCmd(string command) {
        monitorExecute("human-monitor-command", new string[][] { 
            new string[] { "command-line", command }
        });
    }
    private void monitorExecute(string command, string[][] arguments) { 
        //flatten the arguments
        string argumentFlat = "";
        if (arguments != null) {
            for(int c = 0; c < arguments.Length; c++){
                string[] s = arguments[c];
                argumentFlat +=
                    "\"" + s[0] + "\":\"" + s[1] + "\"" + (c == arguments.Length - 1 ? "" : ",");
            }
            if (arguments.Length != 0) {
                argumentFlat = ",\"arguments\":{" + argumentFlat + "}";
            }
        }

        //send command
        monitorWrite("{\"execute\":\"" + command + "\"" + argumentFlat + "}");
    }
    private void monitorWrite(string message) {
        p_Stream.Write(
            System.Text.Encoding.ASCII.GetBytes(message),
            0,
            message.Length);
    }
    private string monitorRead() {
        string buffer = "";

        while (!p_Stream.DataAvailable) ;
        while (p_Stream.DataAvailable ) {
            byte[] read = new byte[1];
            p_Socket.Receive(read);
            buffer += (char)read[0];
        }

        return buffer;
    }
    #endregion

}