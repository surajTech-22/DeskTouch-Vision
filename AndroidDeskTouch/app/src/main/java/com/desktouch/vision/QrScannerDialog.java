package com.desktouch.vision;

import android.annotation.SuppressLint;
import android.app.Dialog;
import android.content.Context;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.graphics.SurfaceTexture;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.Window;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.Collections;
import java.util.EnumMap;
import java.util.Locale;
import java.util.Map;
import java.util.function.Consumer;

/**
 * In-app Camera2 + pure-Java ZXing QR scanner so testers can point their phone camera
 * at Windows' "Scan QR" dialog to connect immediately without typing pairing links.
 */
final class QrScannerDialog {
    static final class DirectQrTarget {
        final String address;
        final String fingerprint;
        final String secret;

        DirectQrTarget(String address, String fingerprint, String secret) {
            this.address = address;
            this.fingerprint = fingerprint;
            this.secret = secret;
        }
    }

    static DirectQrTarget parseDirectQrPayload(String raw) {
        if (raw == null) return null;
        String[] parts = raw.trim().split("\\|");
        if (parts.length != 4 || !"DT1".equalsIgnoreCase(parts[0].trim()))
            return null;
        String ipField = parts[1].trim();
        String fp = parts[2].trim().toUpperCase(Locale.ROOT);
        String secret = parts[3].trim().toUpperCase(Locale.ROOT);
        java.util.List<String> validAddrs = LinkedDisplayHelper.parseCandidateAddresses(ipField);
        if (validAddrs.isEmpty())
            return null;
        boolean validHexSecret = secret.matches("[A-F0-9]{32}");
        boolean validCustomCode = !LinkedDisplayHelper.normalizeCustomPairingCode(secret).isEmpty();
        if (!fp.matches("[A-F0-9]{64}") || (!validHexSecret && !validCustomCode))
            return null;
        return new DirectQrTarget(String.join(",", validAddrs), fp, secret);
    }

    private QrScannerDialog() {}

    static void show(Context context, Consumer<String> onScanned) {
        Dialog dlg = new Dialog(context, android.R.style.Theme_Black_NoTitleBar_Fullscreen);
        dlg.requestWindowFeature(Window.FEATURE_NO_TITLE);

        FrameLayout root = new FrameLayout(context);
        root.setBackgroundColor(Color.BLACK);

        TextureView preview = new TextureView(context);
        root.addView(preview, new FrameLayout.LayoutParams(-1, -1));

        LinearLayout overlay = new LinearLayout(context);
        overlay.setOrientation(LinearLayout.VERTICAL);
        overlay.setPadding(dp(context, 18), dp(context, 24), dp(context, 18), dp(context, 24));

        TextView title = new TextView(context);
        title.setText("Scan Windows Pairing QR Code");
        title.setTextColor(Color.WHITE);
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f);
        title.setGravity(Gravity.CENTER);
        title.setBackgroundColor(0x880F172A);
        title.setPadding(dp(context, 12), dp(context, 10), dp(context, 12), dp(context, 10));
        overlay.addView(title);

        FrameLayout spacer = new FrameLayout(context);
        overlay.addView(spacer, new LinearLayout.LayoutParams(-1, 0, 1f));

        TextView hint = new TextView(context);
        hint.setText("Point at the square QR shown in DeskTouch Windows -> Scan QR");
        hint.setTextColor(0xFFE2E8F0);
        hint.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12.5f);
        hint.setGravity(Gravity.CENTER);
        hint.setBackgroundColor(0x880F172A);
        hint.setPadding(dp(context, 12), dp(context, 8), dp(context, 12), dp(context, 8));
        overlay.addView(hint);

        Button cancel = new Button(context);
        cancel.setText("Close Scanner");
        cancel.setTextColor(Color.WHITE);
        cancel.setBackgroundColor(0xCC1E293B);
        LinearLayout.LayoutParams cLp = new LinearLayout.LayoutParams(-1, -2);
        cLp.topMargin = dp(context, 10);
        overlay.addView(cancel, cLp);

        root.addView(overlay, new FrameLayout.LayoutParams(-1, -1));
        dlg.setContentView(root);

        final HandlerThread bgThread = new HandlerThread("DeskTouch-QR");
        bgThread.start();
        final Handler bgHandler = new Handler(bgThread.getLooper());
        final Handler uiHandler = new Handler(Looper.getMainLooper());
        final MultiFormatReader qrReader = new MultiFormatReader();
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        qrReader.setHints(hints);

        final CameraDevice[] cameraHolder = new CameraDevice[1];
        final ImageReader[] readerHolder = new ImageReader[1];
        final CameraCaptureSession[] sessionHolder = new CameraCaptureSession[1];
        final boolean[] finished = new boolean[1];
        final long[] lastAnalyzeMs = new long[1];

        Runnable cleanup = () -> {
            finished[0] = true;
            try { if (sessionHolder[0] != null) sessionHolder[0].close(); } catch (Throwable ignored) {}
            try { if (cameraHolder[0] != null) cameraHolder[0].close(); } catch (Throwable ignored) {}
            try { if (readerHolder[0] != null) readerHolder[0].close(); } catch (Throwable ignored) {}
            try { bgThread.quitSafely(); } catch (Throwable ignored) {}
        };

        cancel.setOnClickListener(v -> dlg.dismiss());
        dlg.setOnDismissListener(d -> cleanup.run());

        preview.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @SuppressLint("MissingPermission")
            @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int width, int height) {
                try {
                    CameraManager mgr = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
                    if (mgr == null) return;
                    String backCam = null;
                    for (String id : mgr.getCameraIdList()) {
                        CameraCharacteristics cc = mgr.getCameraCharacteristics(id);
                        Integer facing = cc.get(CameraCharacteristics.LENS_FACING);
                        if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) {
                            backCam = id;
                            break;
                        }
                        if (backCam == null) backCam = id;
                    }
                    if (backCam == null) return;

                    final int imgW = 640, imgH = 480;
                    st.setDefaultBufferSize(imgW, imgH);
                    Surface previewSurface = new Surface(st);
                    ImageReader yuvReader = ImageReader.newInstance(imgW, imgH, ImageFormat.YUV_420_888, 2);
                    readerHolder[0] = yuvReader;

                    yuvReader.setOnImageAvailableListener(r -> {
                        Image img = r.acquireLatestImage();
                        if (img == null) return;
                        try {
                            if (finished[0]) return;
                            long now = android.os.SystemClock.uptimeMillis();
                            if (now - lastAnalyzeMs[0] < 130L) return;
                            lastAnalyzeMs[0] = now;

                            Image.Plane yPlane = img.getPlanes()[0];
                            int rowStride = yPlane.getRowStride();
                            int w = img.getWidth();
                            int h = img.getHeight();
                            ByteBuffer buf = yPlane.getBuffer();
                            byte[] rawY = new byte[rowStride * h];
                            buf.get(rawY, 0, Math.min(rawY.length, buf.remaining()));
                            byte[] yData = rowStride == w ? rawY : compactY(rawY, w, h, rowStride);

                            String decoded = tryDecode(qrReader, yData, w, h);
                            if (decoded == null) {
                                byte[] rotated = rotate90CW(yData, w, h);
                                decoded = tryDecode(qrReader, rotated, h, w);
                            }
                            if (decoded != null && !decoded.trim().isEmpty()) {
                                finished[0] = true;
                                final String text = decoded.trim();
                                uiHandler.post(() -> {
                                    if (dlg.isShowing()) dlg.dismiss();
                                    onScanned.accept(text);
                                });
                            }
                        } catch (Throwable ignored) {
                        } finally {
                            img.close();
                        }
                    }, bgHandler);

                    mgr.openCamera(backCam, new CameraDevice.StateCallback() {
                        @Override public void onOpened(CameraDevice cam) {
                            if (finished[0]) { cam.close(); return; }
                            cameraHolder[0] = cam;
                            try {
                                CaptureRequest.Builder req = cam.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                                req.addTarget(previewSurface);
                                req.addTarget(yuvReader.getSurface());
                                req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                                cam.createCaptureSession(
                                    Arrays.asList(previewSurface, yuvReader.getSurface()),
                                    new CameraCaptureSession.StateCallback() {
                                        @Override public void onConfigured(CameraCaptureSession session) {
                                            if (finished[0]) return;
                                            sessionHolder[0] = session;
                                            try { session.setRepeatingRequest(req.build(), null, bgHandler); }
                                            catch (Throwable ignored) {}
                                        }
                                        @Override public void onConfigureFailed(CameraCaptureSession session) {}
                                    }, bgHandler);
                            } catch (Throwable ignored) {}
                        }
                        @Override public void onDisconnected(CameraDevice cam) { cam.close(); }
                        @Override public void onError(CameraDevice cam, int err) { cam.close(); }
                    }, bgHandler);
                } catch (Throwable ignored) {}
            }
            @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) {}
            @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) { return true; }
            @Override public void onSurfaceTextureUpdated(SurfaceTexture st) {}
        });

        dlg.show();
    }

    private static String tryDecode(MultiFormatReader reader, byte[] y, int w, int h) {
        try {
            PlanarYUVLuminanceSource src = new PlanarYUVLuminanceSource(y, w, h, 0, 0, w, h, false);
            Result res = reader.decodeWithState(new BinaryBitmap(new HybridBinarizer(src)));
            reader.reset();
            return res != null ? res.getText() : null;
        } catch (Throwable ignored) {
            reader.reset();
            return null;
        }
    }

    private static byte[] compactY(byte[] src, int w, int h, int stride) {
        byte[] dst = new byte[w * h];
        for (int row = 0; row < h; row++) {
            System.arraycopy(src, row * stride, dst, row * w, Math.min(w, Math.max(0, src.length - row * stride)));
        }
        return dst;
    }

    private static byte[] rotate90CW(byte[] src, int w, int h) {
        byte[] dst = new byte[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                dst[x * h + (h - 1 - y)] = src[y * w + x];
            }
        }
        return dst;
    }

    private static int dp(Context ctx, int v) {
        return Math.round(v * ctx.getResources().getDisplayMetrics().density);
    }
}
