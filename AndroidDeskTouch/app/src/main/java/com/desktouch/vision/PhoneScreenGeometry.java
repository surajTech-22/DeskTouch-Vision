package com.desktouch.vision;

import android.content.Context;
import android.content.pm.PackageInfo;
import android.graphics.Point;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.util.DisplayMetrics;
import android.view.Display;
import android.view.Surface;
import android.view.WindowManager;

/**
 * One physical-screen coordinate system for TLS topology, pointer overlay,
 * Accessibility gestures and node bounds, plus rich display/device telemetry.
 */
final class PhoneScreenGeometry {
    static final class Info {
        final int width;
        final int height;
        final int densityDpi;
        final int refreshHz;
        final int rotationDegrees;
        final String orientation;
        final String deviceName;
        final String osVersion;
        final String appVersion;

        Info(int width,int height,int densityDpi,int refreshHz,int rotationDegrees,
                String deviceName,String osVersion,String appVersion) {
            this.width=Math.max(1,width);
            this.height=Math.max(1,height);
            this.densityDpi=Math.max(0,densityDpi);
            this.refreshHz=Math.max(0,refreshHz);
            this.rotationDegrees=rotationDegrees;
            this.orientation=orientationName(this.width,this.height);
            this.deviceName=deviceName==null ? "" : deviceName;
            this.osVersion=osVersion==null ? "" : osVersion;
            this.appVersion=appVersion==null ? "" : appVersion;
        }
    }

    private PhoneScreenGeometry() {}

    static int rotationDegrees(int surfaceRotation) {
        switch(surfaceRotation) {
            case Surface.ROTATION_90: return 90;
            case Surface.ROTATION_180: return 180;
            case Surface.ROTATION_270: return 270;
            default: return 0;
        }
    }

    static String orientationName(int width,int height) {
        return width>height ? "Landscape" : "Portrait";
    }

    static String cleanDeviceName(String manufacturer,String model) {
        String mfg=manufacturer==null ? "" : manufacturer.trim();
        String mdl=model==null ? "" : model.trim();
        if(mfg.isEmpty()) return mdl.isEmpty() ? "Android phone" : mdl;
        if(mdl.isEmpty()) return mfg;
        if(mdl.regionMatches(true,0,mfg,0,mfg.length())) return mdl;
        return mfg+" "+mdl;
    }

    static Display getDefaultDisplay(Context context) {
        if(context==null) return null;
        Display display=null;
        DisplayManager dm=(DisplayManager)context.getSystemService(Context.DISPLAY_SERVICE);
        if(dm!=null) {
            try { display=dm.getDisplay(Display.DEFAULT_DISPLAY); }
            catch(RuntimeException ignored) {}
        }
        if(display==null) {
            WindowManager manager=(WindowManager)context.getSystemService(Context.WINDOW_SERVICE);
            if(manager!=null) {
                try { display=manager.getDefaultDisplay(); }
                catch(RuntimeException ignored) {}
            }
        }
        return display;
    }

    static int readDisplayRefreshHz(Display display) {
        if(display==null) return 0;
        float hz=0f;
        if(Build.VERSION.SDK_INT>=Build.VERSION_CODES.M) {
            try {
                Display.Mode mode=display.getMode();
                if(mode!=null) hz=mode.getRefreshRate();
            } catch(RuntimeException ignored) {}
        }
        if(hz<20f || hz>360f) {
            try { hz=display.getRefreshRate(); }
            catch(RuntimeException ignored) {}
        }
        if(hz>=20f && hz<=360f) return PointerSmoother.snapNearestStandardHz(Math.round(hz));
        return 0;
    }

    static int readLiveRefreshHz(Context context) {
        return readDisplayRefreshHz(getDefaultDisplay(context));
    }

    static float readPreferredActiveRefreshRate(Context context) {
        Display display=getDefaultDisplay(context);
        if(display==null || Build.VERSION.SDK_INT<Build.VERSION_CODES.M) return 0f;
        try {
            Display.Mode active=display.getMode();
            Display.Mode[] modes=display.getSupportedModes();
            if(active==null || modes==null) return 0f;
            int physW=active.getPhysicalWidth();
            int physH=active.getPhysicalHeight();
            float best=0f;
            for(Display.Mode mode : modes) {
                if(mode==null) continue;
                if(physW>0 && physH>0 &&
                        (mode.getPhysicalWidth()!=physW || mode.getPhysicalHeight()!=physH)) {
                    continue;
                }
                float rate=mode.getRefreshRate();
                if(rate>60.5f && rate<=240.5f && rate>best) {
                    best=rate;
                }
            }
            return best;
        } catch(RuntimeException ignored) {
            return 0f;
        }
    }

    static Info readInfo(Context context) {
        Display display=getDefaultDisplay(context);
        WindowManager manager=(WindowManager)context.getSystemService(Context.WINDOW_SERVICE);
        DisplayMetrics resMetrics=context.getResources().getDisplayMetrics();
        int width=0,height=0,dpi=resMetrics.densityDpi,refreshHz=0,rotation=0;
        if(display!=null) {
            try {
                DisplayMetrics real=new DisplayMetrics();
                display.getRealMetrics(real);
                if(real.widthPixels>0 && real.heightPixels>0) {
                    width=real.widthPixels;
                    height=real.heightPixels;
                }
                if(real.densityDpi>0) dpi=real.densityDpi;
            } catch(RuntimeException ignored) {}
            if(width<=0 || height<=0) {
                try {
                    Point actual=new Point();
                    display.getRealSize(actual);
                    if(actual.x>0 && actual.y>0) { width=actual.x; height=actual.y; }
                } catch(RuntimeException ignored) {}
            }
            try { rotation=rotationDegrees(display.getRotation()); }
            catch(RuntimeException ignored) {}
            refreshHz=readDisplayRefreshHz(display);
        }
        if((width<=0 || height<=0) && manager!=null && Build.VERSION.SDK_INT>=30) {
            try {
                Rect bounds=manager.getMaximumWindowMetrics().getBounds();
                if(bounds.width()>0 && bounds.height()>0) {
                    width=bounds.width();
                    height=bounds.height();
                }
            } catch(RuntimeException ignored) {}
        }
        if(width<=0 || height<=0) {
            width=Math.max(1,resMetrics.widthPixels);
            height=Math.max(1,resMetrics.heightPixels);
        }
        String release=Build.VERSION.RELEASE==null ? "" : Build.VERSION.RELEASE.trim();
        String osVersion=release.isEmpty() ? "Android" : "Android "+release;
        String appVersion="";
        try {
            PackageInfo pkg=context.getPackageManager()
                .getPackageInfo(context.getPackageName(),0);
            if(pkg!=null && pkg.versionName!=null) appVersion=pkg.versionName;
        } catch(Exception ignored) {}
        return new Info(width,height,dpi,refreshHz,rotation,
            cleanDeviceName(Build.MANUFACTURER,Build.MODEL),osVersion,appVersion);
    }

    static Point read(Context context) {
        Info info=readInfo(context);
        return new Point(info.width,info.height);
    }
}
