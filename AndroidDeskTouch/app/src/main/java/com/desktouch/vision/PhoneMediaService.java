package com.desktouch.vision;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.ImageFormat;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.YuvImage;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.Image;
import android.media.ImageReader;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.Collections;

/**
 * Explicit-consent Android foreground service for live Phone Screen mirroring
 * (MediaProjection) and Wireless Phone Camera + Microphone streaming (Camera2 + AudioRecord).
 * Pauses screen frames automatically during payment/banking apps.
 */
public final class PhoneMediaService extends Service {
    private static final String CHANNEL_ID = "desktouch_media_share";
    private static final int NOTIF_ID = 3849;
    static final String ACTION_START_SCREEN = "com.desktouch.vision.START_SCREEN";
    static final String ACTION_STOP_SCREEN = "com.desktouch.vision.STOP_SCREEN";
    static final String ACTION_START_CAMERA = "com.desktouch.vision.START_CAMERA";
    static final String ACTION_STOP_CAMERA = "com.desktouch.vision.STOP_CAMERA";
    static final String ACTION_STOP_ALL = "com.desktouch.vision.STOP_MEDIA_ALL";
    static final String EXTRA_RESULT_CODE = "result_code";
    static final String EXTRA_PROJECTION_DATA = "projection_data";
    static final String EXTRA_LENS = "lens";
    static final String EXTRA_TORCH = "torch";
    static final String EXTRA_MIC = "mic";

    interface MediaSink {
        void onScreenFrame(byte[] jpeg);
        void onScreenStatus(int stateCode, String msg);
        void onCameraFrame(byte[] jpeg);
        void onCameraStatus(int stateCode, String msg);
        void onMicAudio(byte[] pcm16Mono, int sampleRate);
    }

    private static volatile MediaSink activeSink;
    private static volatile boolean screenRunning;
    private static volatile boolean cameraRunning;
    private static volatile boolean pendingWindowsScreenRequest;
    private static volatile boolean pendingWindowsCameraRequest;
    private static volatile int desiredLens;
    private static volatile boolean desiredTorch;
    private static volatile boolean desiredMic;

    private HandlerThread mediaThread;
    private Handler mediaHandler;

    private MediaProjection projection;
    private VirtualDisplay virtualDisplay;
    private ImageReader screenReader;
    private long lastScreenFrameMs;
    private long lastPrivacyNoticeMs;

    private CameraDevice cameraDevice;
    private CameraCaptureSession cameraSession;
    private ImageReader cameraReader;
    private int openedLens = -1;
    private boolean openedTorch;
    private long lastCamFrameMs;

    private volatile boolean micLoopRunning;
    private Thread micThread;

    static void setSink(MediaSink sink) {
        activeSink = sink;
    }

    static boolean isScreenSharingActive() { return screenRunning; }
    static boolean isCameraSharingActive() { return cameraRunning; }

    static void startScreenShare(Context ctx, int resultCode, Intent data) {
        Intent i = new Intent(ctx, PhoneMediaService.class);
        i.setAction(ACTION_START_SCREEN);
        i.putExtra(EXTRA_RESULT_CODE, resultCode);
        i.putExtra(EXTRA_PROJECTION_DATA, data);
        startForegroundCompat(ctx, i);
    }

    static void stopScreenShare(Context ctx) {
        if (!screenRunning && !cameraRunning) return;
        Intent i = new Intent(ctx, PhoneMediaService.class);
        i.setAction(ACTION_STOP_SCREEN);
        try { ctx.startService(i); } catch (Throwable ignored) {}
    }

    static void startCameraShare(Context ctx, int lens, boolean torch, boolean mic) {
        desiredLens = lens;
        desiredTorch = torch;
        desiredMic = mic;
        Intent i = new Intent(ctx, PhoneMediaService.class);
        i.setAction(ACTION_START_CAMERA);
        i.putExtra(EXTRA_LENS, lens);
        i.putExtra(EXTRA_TORCH, torch);
        i.putExtra(EXTRA_MIC, mic);
        startForegroundCompat(ctx, i);
    }

    static void stopCameraShare(Context ctx) {
        if (!cameraRunning && !screenRunning) return;
        Intent i = new Intent(ctx, PhoneMediaService.class);
        i.setAction(ACTION_STOP_CAMERA);
        try { ctx.startService(i); } catch (Throwable ignored) {}
    }

    static void stopAll(Context ctx) {
        screenRunning = false;
        cameraRunning = false;
        if (ctx != null) {
            try { ctx.stopService(new Intent(ctx, PhoneMediaService.class)); }
            catch (Throwable ignored) {}
        }
    }

    private static void startForegroundCompat(Context ctx, Intent i) {
        try {
            if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i);
            else ctx.startService(i);
        } catch (Throwable ignored) {}
    }

    @Override public void onCreate() {
        super.onCreate();
        mediaThread = new HandlerThread("DeskTouch-MediaShare");
        mediaThread.start();
        mediaHandler = new Handler(mediaThread.getLooper());
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : null;
        if (ACTION_STOP_ALL.equals(action)) {
            stopScreenInternal();
            stopCameraInternal();
            stopSelfCleanly();
            return START_NOT_STICKY;
        }
        if (ACTION_STOP_SCREEN.equals(action)) {
            stopScreenInternal();
            if (!cameraRunning) stopSelfCleanly();
            else promoteForeground();
            return START_NOT_STICKY;
        }
        if (ACTION_STOP_CAMERA.equals(action)) {
            stopCameraInternal();
            if (!screenRunning) stopSelfCleanly();
            else promoteForeground();
            return START_NOT_STICKY;
        }
        if (ACTION_START_SCREEN.equals(action)) {
            int code = intent.getIntExtra(EXTRA_RESULT_CODE, Activity.RESULT_CANCELED);
            Intent data = intent.getParcelableExtra(EXTRA_PROJECTION_DATA);
            if (code == Activity.RESULT_OK && data != null) {
                screenRunning = true;
                promoteForeground();
                startProjection(code, data);
            } else if (!cameraRunning) {
                stopSelfCleanly();
            }
            return START_NOT_STICKY;
        }
        if (ACTION_START_CAMERA.equals(action)) {
            int lens = intent.getIntExtra(EXTRA_LENS, desiredLens);
            boolean torch = intent.getBooleanExtra(EXTRA_TORCH, desiredTorch);
            boolean mic = intent.getBooleanExtra(EXTRA_MIC, desiredMic);
            boolean hasCam = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED;
            if (!hasCam) {
                emitCameraStatus(2, "Grant Camera permission on Android");
                if (!screenRunning) stopSelfCleanly();
                return START_NOT_STICKY;
            }
            cameraRunning = true;
            promoteForeground();
            startCameraAndMic(lens, torch, mic);
            return START_NOT_STICKY;
        }
        promoteForeground();
        return START_NOT_STICKY;
    }

    private void promoteForeground() {
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26 && nm != null && nm.getNotificationChannel(CHANNEL_ID) == null) {
            NotificationChannel ch = new NotificationChannel(CHANNEL_ID, "DeskTouch Screen & Camera Share",
                NotificationManager.IMPORTANCE_LOW);
            ch.setShowBadge(false);
            nm.createNotificationChannel(ch);
        }
        Intent stopIntent = new Intent(this, PhoneMediaService.class).setAction(ACTION_STOP_ALL);
        PendingIntent stopPi = PendingIntent.getService(this, 11, stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        String msg = screenRunning && cameraRunning
            ? "Sharing phone screen and camera with Windows"
            : (screenRunning ? "Mirroring phone screen to Windows" : "Streaming phone camera/mic to Windows");
        Notification.Builder b = Build.VERSION.SDK_INT >= 26
            ? new Notification.Builder(this, CHANNEL_ID)
            : new Notification.Builder(this);
        Notification n = b.setSmallIcon(R.drawable.ic_desktouch)
            .setContentTitle("DeskTouch Vision Live Share")
            .setContentText(msg)
            .setOngoing(true)
            .addAction(0, "Stop Sharing", stopPi)
            .build();
        if (Build.VERSION.SDK_INT >= 29) {
            int mask = 0;
            if (screenRunning) mask |= ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION;
            if (Build.VERSION.SDK_INT >= 30 && cameraRunning) {
                if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
                    mask |= ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA;
                if (desiredMic && checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
                    mask |= ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE;
            }
            if (mask == 0) mask = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION;
            startForeground(NOTIF_ID, n, mask);
        } else {
            startForeground(NOTIF_ID, n);
        }
    }

    private void startProjection(int resultCode, Intent data) {
        stopScreenInternal();
        screenRunning = true;
        try {
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            if (mpm == null) { emitScreenStatus(0, "Screen share unavailable"); return; }
            projection = mpm.getMediaProjection(resultCode, data);
            if (projection == null) { emitScreenStatus(0, "Screen share declined"); return; }
            projection.registerCallback(new MediaProjection.Callback() {
                @Override public void onStop() {
                    stopScreenInternal();
                    emitScreenStatus(0, "Screen share stopped");
                }
            }, mediaHandler);

            DisplayMetrics dm = getResources().getDisplayMetrics();
            int rawW = Math.max(360, dm.widthPixels);
            int rawH = Math.max(640, dm.heightPixels);
            double scale = Math.min(1.0, 540.0 / Math.max(rawW, rawH));
            int capW = Math.max(180, ((int) Math.round(rawW * scale)) & ~1);
            int capH = Math.max(240, ((int) Math.round(rawH * scale)) & ~1);

            screenReader = ImageReader.newInstance(capW, capH, PixelFormat.RGBA_8888, 2);
            screenReader.setOnImageAvailableListener(reader -> {
                Image img = reader.acquireLatestImage();
                if (img == null) return;
                try {
                    if (!screenRunning) return;
                    long now = SystemClock.uptimeMillis();
                    if (DeskTouchAccessibilityService.isPausedForBanking(this)) {
                        if (now - lastPrivacyNoticeMs >= 1500L) {
                            lastPrivacyNoticeMs = now;
                            emitScreenStatus(3, "Paused for payment privacy");
                        }
                        return;
                    }
                    if (now - lastScreenFrameMs < 135L) return;
                    lastScreenFrameMs = now;

                    Image.Plane plane = img.getPlanes()[0];
                    ByteBuffer buffer = plane.getBuffer();
                    int pixelStride = plane.getPixelStride();
                    int rowStride = plane.getRowStride();
                    int rowPadding = rowStride - pixelStride * capW;
                    Bitmap full = Bitmap.createBitmap(capW + rowPadding / Math.max(1, pixelStride), capH, Bitmap.Config.ARGB_8888);
                    full.copyPixelsFromBuffer(buffer);
                    Bitmap cropped = rowPadding > 0 ? Bitmap.createBitmap(full, 0, 0, capW, capH) : full;
                    if (cropped != full) full.recycle();
                    ByteArrayOutputStream baos = new ByteArrayOutputStream(24 * 1024);
                    cropped.compress(Bitmap.CompressFormat.JPEG, 62, baos);
                    cropped.recycle();
                    byte[] jpeg = baos.toByteArray();
                    if (jpeg.length > 0 && jpeg.length <= 160 * 1024) {
                        MediaSink sink = activeSink;
                        if (sink != null) sink.onScreenFrame(jpeg);
                    }
                } catch (Throwable ignored) {
                } finally {
                    img.close();
                }
            }, mediaHandler);

            virtualDisplay = projection.createVirtualDisplay(
                "DeskTouchScreen", capW, capH, dm.densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                screenReader.getSurface(), null, mediaHandler);
            emitScreenStatus(1, "Live");
        } catch (Throwable ex) {
            emitScreenStatus(0, "Screen capture error: " + ex.getMessage());
            stopScreenInternal();
        }
    }

    @SuppressLint("MissingPermission")
    private void startCameraAndMic(int lens, boolean torch, boolean mic) {
        if (mic && checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            startMicLoop();
        } else {
            stopMicLoop();
        }

        if (cameraDevice != null && openedLens == lens) {
            updateTorchRequest(torch);
            return;
        }
        closeCameraOnly();
        openedLens = lens;
        openedTorch = torch;
        try {
            CameraManager mgr = (CameraManager) getSystemService(CAMERA_SERVICE);
            if (mgr == null) { emitCameraStatus(0, "No camera"); return; }
            int targetFacing = lens == 1
                ? CameraCharacteristics.LENS_FACING_FRONT
                : CameraCharacteristics.LENS_FACING_BACK;
            String camId = null;
            for (String id : mgr.getCameraIdList()) {
                CameraCharacteristics cc = mgr.getCameraCharacteristics(id);
                Integer f = cc.get(CameraCharacteristics.LENS_FACING);
                if (f != null && f == targetFacing) { camId = id; break; }
                if (camId == null) camId = id;
            }
            if (camId == null) { emitCameraStatus(0, "No camera sensor"); return; }
            final int w = 640, h = 480;
            cameraReader = ImageReader.newInstance(w, h, ImageFormat.YUV_420_888, 2);
            cameraReader.setOnImageAvailableListener(reader -> {
                Image img = reader.acquireLatestImage();
                if (img == null) return;
                try {
                    if (!cameraRunning) return;
                    long now = SystemClock.uptimeMillis();
                    if (now - lastCamFrameMs < 95L) return;
                    lastCamFrameMs = now;
                    byte[] nv21 = yuv420ToNv21(img);
                    YuvImage yuv = new YuvImage(nv21, ImageFormat.NV21, img.getWidth(), img.getHeight(), null);
                    ByteArrayOutputStream out = new ByteArrayOutputStream(28 * 1024);
                    yuv.compressToJpeg(new Rect(0, 0, img.getWidth(), img.getHeight()), 60, out);
                    byte[] jpeg = out.toByteArray();
                    if (jpeg.length > 0 && jpeg.length <= 160 * 1024) {
                        MediaSink sink = activeSink;
                        if (sink != null) sink.onCameraFrame(jpeg);
                    }
                } catch (Throwable ignored) {
                } finally {
                    img.close();
                }
            }, mediaHandler);

            mgr.openCamera(camId, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice cam) {
                    if (!cameraRunning) { cam.close(); return; }
                    cameraDevice = cam;
                    try {
                        CaptureRequest.Builder req = cam.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                        req.addTarget(cameraReader.getSurface());
                        req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
                        if (openedTorch && openedLens == 0)
                            req.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_TORCH);
                        cam.createCaptureSession(
                            Collections.singletonList(cameraReader.getSurface()),
                            new CameraCaptureSession.StateCallback() {
                                @Override public void onConfigured(CameraCaptureSession session) {
                                    if (!cameraRunning) return;
                                    cameraSession = session;
                                    try {
                                        session.setRepeatingRequest(req.build(), null, mediaHandler);
                                        emitCameraStatus(1, "Live camera ready");
                                    } catch (Throwable ignored) {}
                                }
                                @Override public void onConfigureFailed(CameraCaptureSession session) {}
                            }, mediaHandler);
                    } catch (Throwable ex) {
                        emitCameraStatus(0, "Camera error: " + ex.getMessage());
                    }
                }
                @Override public void onDisconnected(CameraDevice cam) { cam.close(); cameraDevice = null; }
                @Override public void onError(CameraDevice cam, int error) { cam.close(); cameraDevice = null; }
            }, mediaHandler);
        } catch (Throwable ex) {
            emitCameraStatus(0, "Camera unavailable: " + ex.getMessage());
        }
    }

    private void updateTorchRequest(boolean torch) {
        openedTorch = torch;
        if (cameraDevice == null || cameraSession == null || cameraReader == null) return;
        try {
            CaptureRequest.Builder req = cameraDevice.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
            req.addTarget(cameraReader.getSurface());
            req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
            req.set(CaptureRequest.FLASH_MODE,
                (torch && openedLens == 0) ? CaptureRequest.FLASH_MODE_TORCH : CaptureRequest.FLASH_MODE_OFF);
            cameraSession.setRepeatingRequest(req.build(), null, mediaHandler);
        } catch (Throwable ignored) {}
    }

    @SuppressLint("MissingPermission")
    private void startMicLoop() {
        if (micLoopRunning) return;
        micLoopRunning = true;
        micThread = new Thread(() -> {
            final int rate = 16000;
            final int minBuf = AudioRecord.getMinBufferSize(
                rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            AudioRecord rec = null;
            try {
                rec = new AudioRecord(MediaRecorder.AudioSource.MIC, rate,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
                    Math.max(minBuf, 4096));
                if (rec.getState() != AudioRecord.STATE_INITIALIZED) return;
                rec.startRecording();
                byte[] chunk = new byte[1280]; // 40ms PCM16 mono @ 16kHz -> ~1.7KB Base64 (fits well inside 8192 Packet)
                while (micLoopRunning && cameraRunning) {
                    int n = rec.read(chunk, 0, chunk.length);
                    if (n > 0) {
                        byte[] exact = new byte[n];
                        System.arraycopy(chunk, 0, exact, 0, n);
                        MediaSink sink = activeSink;
                        if (sink != null) sink.onMicAudio(exact, rate);
                    }
                }
            } catch (Throwable ignored) {
            } finally {
                if (rec != null) {
                    try { rec.stop(); } catch (Throwable ignored) {}
                    try { rec.release(); } catch (Throwable ignored) {}
                }
            }
        }, "DeskTouch-MicStream");
        micThread.setDaemon(true);
        micThread.start();
    }

    private void stopMicLoop() {
        micLoopRunning = false;
        if (micThread != null) {
            micThread.interrupt();
            micThread = null;
        }
    }

    private void stopScreenInternal() {
        screenRunning = false;
        try { if (virtualDisplay != null) virtualDisplay.release(); } catch (Throwable ignored) {}
        virtualDisplay = null;
        try { if (screenReader != null) screenReader.close(); } catch (Throwable ignored) {}
        screenReader = null;
        try { if (projection != null) projection.stop(); } catch (Throwable ignored) {}
        projection = null;
    }

    private void closeCameraOnly() {
        try { if (cameraSession != null) cameraSession.close(); } catch (Throwable ignored) {}
        cameraSession = null;
        try { if (cameraDevice != null) cameraDevice.close(); } catch (Throwable ignored) {}
        cameraDevice = null;
        try { if (cameraReader != null) cameraReader.close(); } catch (Throwable ignored) {}
        cameraReader = null;
        openedLens = -1;
    }

    private void stopCameraInternal() {
        cameraRunning = false;
        stopMicLoop();
        closeCameraOnly();
    }

    private void stopSelfCleanly() {
        try { stopForeground(STOP_FOREGROUND_REMOVE); } catch (Throwable ignored) {}
        stopSelf();
    }

    private static void emitScreenStatus(int stateCode, String msg) {
        MediaSink sink = activeSink;
        if (sink != null) sink.onScreenStatus(stateCode, msg);
    }

    private static void emitCameraStatus(int stateCode, String msg) {
        MediaSink sink = activeSink;
        if (sink != null) sink.onCameraStatus(stateCode, msg);
    }

    private static byte[] yuv420ToNv21(Image image) {
        int w = image.getWidth();
        int h = image.getHeight();
        int ySize = w * h;
        int uvSize = ySize / 2;
        byte[] nv21 = new byte[ySize + uvSize];

        Image.Plane[] planes = image.getPlanes();
        ByteBuffer yBuf = planes[0].getBuffer();
        ByteBuffer uBuf = planes[1].getBuffer();
        ByteBuffer vBuf = planes[2].getBuffer();
        int yStride = planes[0].getRowStride();
        int uvStride = planes[1].getRowStride();
        int uvPixelStride = planes[1].getPixelStride();

        int pos = 0;
        for (int row = 0; row < h; row++) {
            yBuf.position(row * yStride);
            yBuf.get(nv21, pos, w);
            pos += w;
        }
        int halfH = h / 2, halfW = w / 2;
        for (int row = 0; row < halfH; row++) {
            int rowBase = row * uvStride;
            for (int col = 0; col < halfW; col++) {
                int idx = rowBase + col * uvPixelStride;
                nv21[pos++] = vBuf.get(idx);
                nv21[pos++] = uBuf.get(idx);
            }
        }
        return nv21;
    }

    @Override public void onDestroy() {
        stopScreenInternal();
        stopCameraInternal();
        if (mediaThread != null) {
            try { mediaThread.quitSafely(); } catch (Throwable ignored) {}
        }
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
