using Martix.HikNetCamera.Native;
using System.Runtime.InteropServices;
using System.Text;

namespace Martix.HikNetCamera;


/// <summary>
/// 相机接口
/// InitializeSdk(程序启动) => Login => Dispose => CleanUpSdk(程序结束)
/// </summary>
public sealed class CameraDevice : IDisposable
{
    public delegate void VideoStreamCallBack(VideoStreamType streamType, byte[] data);

    /// <summary>
    /// 视频数据流类型
    /// </summary>
    public enum VideoStreamType
    {
        Header = 0,
        Body
    };


    public string Password => _password;

    public string UserName => _userName;

    public string DeviceAddress => _deviceAddress;

    public ushort Port => _port;



    /// <summary>
    /// 初始化海康SDK，应在应用程序初始化时调用
    /// </summary>
    /// <exception cref="Exception">失败，抛出异常</exception>
    public static void InitializeSdk()
    {
        if (!HCNetSDK.NET_DVR_Init())
        {
            throw new Exception("Hik Code=" + HCNetSDK.NET_DVR_GetLastError());
        }
        HCNetSDK.NET_DVR_SetConnectTime(2000, 1);//设置超时时间
        HCNetSDK.NET_DVR_SetReconnect(10000, 1);//设置重连时
    }

    /// <summary>
    /// 销毁海康SDK，应在应用程序结束调用
    /// </summary>
    public static void CleanUpSdk()
    {
        HCNetSDK.NET_DVR_Cleanup();
    }

    public CameraDevice(string userName, string password, string deviceAddress, ushort port = 8000)
    {
        _userName = userName;
        _password = password;
        _deviceAddress = deviceAddress;
        _port = port;
    }

    ~CameraDevice()
    {
        Dispose();
    }

    public void Dispose()
    {
        HCNetSDK.NET_DVR_Logout(_userId);
        _userId = -1;
        _realPlayHandle = -1;
    }

    /// <summary>
    /// 登录相机，阻塞线程
    /// </summary>
    /// <exception cref="Exception">登录失败</exception>
    public void Login()
    {
        byte[] bytesUserName = Encoding.Default.GetBytes(UserName);
        byte[] bytesPassword = Encoding.Default.GetBytes(Password);
        byte[] bytesDeviceAddress = Encoding.Default.GetBytes(DeviceAddress);

        byte[] inputUserName = new byte[HCNetSDK.NET_DVR_LOGIN_USERNAME_MAX_LEN];
        byte[] inputPassword = new byte[HCNetSDK.NET_DVR_LOGIN_PASSWD_MAX_LEN];
        byte[] inputDeviceAddress = new byte[HCNetSDK.NET_DVR_DEV_ADDRESS_MAX_LEN];

        Array.Copy(bytesUserName, inputUserName, bytesUserName.Length);
        Array.Copy(bytesPassword, inputPassword, bytesPassword.Length);
        Array.Copy(bytesDeviceAddress, inputDeviceAddress, bytesDeviceAddress.Length);
        _loginInfo = new()
        {
            sDeviceAddress = inputDeviceAddress,
            wPort = Port,
            sUserName = inputUserName,
            sPassword = inputPassword,
            // 异步登录使用回调
            //cbLoginResult = (int lUserID, int dwResult, IntPtr lpDeviceInfo, IntPtr pUser) =>
            //{

            //}
        };

        _deviceInfo = new();

        _userId = HCNetSDK.NET_DVR_Login_V40(ref _loginInfo, ref _deviceInfo);
        if (_userId == -1)
        {
            throw new Exception("Hik Code=" + GetLastErrorCode());
        }

    }


    /// <summary>
    /// 开启视频取流，RTSP, UDP(GB28181规范)
    /// <paramref name="callback">回调函数</paramref>
    /// <paramref name = "isSubStream" > 是否是子码流 </paramref>
    /// </ summary >
    public void StartFetchVideoStream(VideoStreamCallBack callback, bool isSubStream = false)
    {
        if (_realPlayHandle != -1)
        {
            HCNetSDK.NET_DVR_StopRealPlay(_realPlayHandle);
        }

        HCNetSDK.NET_DVR_PREVIEWINFO previewInfo = new()
        {
            lChannel = _deviceInfo.struDeviceV30.byStartChan,//预览的设备通道 the device channel number
            dwStreamType = isSubStream ? 1u : 0u,//码流类型：0-主码流，1-子码流，2-码流3，3-码流4，以此类推
            dwLinkMode = 1, //连接方式：0- TCP方式，1- UDP方式，2- 多播方式，3- RTP方式，4-RTP/RTSP，5-RSTP/HTTP 
            byProtoType = 1,// 应用层取流协议 0私有协议 1RTSP协议
            bBlocked = true, //0- 非阻塞取流，1- 阻塞取流
        };

        _realTimeStreamCallBack = new(callback);// 复制，防止callback已经被垃圾回收


        // 强制I帧
        //CHCNetSDK.NET_DVR_MakeKeyFrame(_userId, _deviceInfo.struDeviceV30.byStartChan);

        // 默认PS流封装
        _realPlayHandle = HCNetSDK.NET_DVR_RealPlay_V40(_userId, ref previewInfo, RealPlayCallback, 0);
        if (_realPlayHandle == -1)
        {
            //_realPlayStopwatch.Stop();
            throw new Exception("Hik Code=" + GetLastErrorCode());
        };
    }

    /// <summary>
    /// 停止视频取流
    /// </summary>
    public void StopStartFetchVideoStream()
    {
        HCNetSDK.NET_DVR_StopRealPlay(_realPlayHandle);
        _realPlayHandle = -1;
    }

    /// <summary>
    /// 直接抓取图片,无需启动实时数据流获取，阻塞线程
    /// </summary>
    public byte[] DirectlyCaptureJpegImage()
    {
        HCNetSDK.NET_DVR_JPEGPARA outJpegParam = new();
        byte[] buffer = new byte[1024 * 1024 * 50];// 50M
        uint size = 0;
        bool ok = HCNetSDK.NET_DVR_CaptureJPEGPicture_NEW(_userId, _deviceInfo.struDeviceV30.byStartChan, ref outJpegParam, buffer,
            (uint)buffer.Length, ref size);
        if (!ok) throw new Exception("Hik Code=" + GetLastErrorCode());
        byte[] ret = new byte[size];
        Array.Copy(buffer, ret, size);
        return ret;
    }

    /// <summary>
    /// 获取海康SDK的错误码
    /// </summary>
    public uint GetLastErrorCode()
    {
        return HCNetSDK.NET_DVR_GetLastError();
    }

    /// <summary>
    /// 视频取流回调
    /// </summary>
    /// <param name="realHandle"></param>
    /// <param name="dataType">数据类型</param>
    /// <param name="pBuffer">缓冲区</param>
    /// <param name="bufSize">缓冲区大小</param>
    /// <param name="pUser">用户传入数据（暂未使用）</param>
    private void RealPlayCallback(int realHandle, uint dataType, nint pBuffer, uint bufSize, nint pUser)
    {

        // 缓冲区为空
        if (bufSize == 0 || pBuffer == 0)
        {
            return;
        }

        // Copy To C# byte[]
        byte[] outBuffer = new byte[bufSize];
        Marshal.Copy(source: pBuffer, destination: outBuffer, startIndex: 0, length: (int)bufSize);


        // 数据类型
        switch (dataType)
        {
            // 流数据
            case HCNetSDK.NET_DVR_STREAMDATA:
                _realTimeStreamCallBack(VideoStreamType.Body, outBuffer);
                break;
            // 头数据
            case HCNetSDK.NET_DVR_SYSHEAD:
                _realTimeStreamCallBack(VideoStreamType.Header, outBuffer);
                break;

            default:
                // 其他类型
                break;
        }
    }






    private VideoStreamCallBack _realTimeStreamCallBack = (_, _) => { };
    private readonly string _userName = string.Empty;
    private readonly string _password = string.Empty;
    private readonly string _deviceAddress = string.Empty;// 一般指IP
    private readonly ushort _port = 0;

    // Login时初始化
    private HCNetSDK.NET_DVR_USER_LOGIN_INFO _loginInfo = new();
    // Login时初始化
    private HCNetSDK.NET_DVR_DEVICEINFO_V40 _deviceInfo = new();
    private int _userId = -1;
    private int _realPlayHandle = -1;
}
