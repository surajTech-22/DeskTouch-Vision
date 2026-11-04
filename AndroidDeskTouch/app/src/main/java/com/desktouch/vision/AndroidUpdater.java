package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.content.pm.*;
import android.net.Uri;
import android.os.*;
import android.provider.Settings;
import android.util.Log;
import androidx.core.content.FileProvider;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.*;

/**
 * Official GitHub release updater. Auto-check and verified APK download run
 * in the background; replacing Android apps always requires OS approval.
 * A debug preview with a different signing certificate CANNOT be updated in
 * place: fail transparently, do not uninstall apps or circumvent signatures.
 */
final class AndroidUpdater {
    private static final String API=
        "https://api.github.com/repos/surajTech-22/DeskTouch-Vision/releases/latest";
    private static final String PREFIX=
        "https://github.com/surajTech-22/DeskTouch-Vision/releases/download/";
    private static final String APK="DeskTouchVision-android-preview.apk";
    private static final String HASH=APK+".sha256";
    private static final String CHANNEL="desktouch_android_updates";
    private static final String PREFS="desktouch_updates";
    private static final ExecutorService WORK=Executors.newSingleThreadExecutor();
    private static volatile Release latest;
    private static volatile boolean busy;

    private static final class Release {
        final String version,apk,shaUrl;
        Release(String version,String apk,String shaUrl){
            this.version=version;this.apk=apk;this.shaUrl=shaUrl;
        }
    }
    private AndroidUpdater(){}

    static int compareVersions(String a,String b){
        String[] l=a.split("\\."),r=b.split("\\.");
        for(int i=0;i<3;i++){
            int x=i<l.length?Integer.parseInt(l[i]):0;
            int y=i<r.length?Integer.parseInt(r[i]):0;
            if(x!=y)return Integer.compare(x,y);
        }
        return 0;
    }
    private static String installedVersion(Context context){
        try {
            return context.getPackageManager().getPackageInfo(context.getPackageName(),0).versionName;
        } catch(Exception e){return "0.0.0";}
    }
    private static String fetchText(String url,int maxBytes) throws Exception{
        URL target=new URL(url);
        if(!"https".equals(target.getProtocol()))
            throw new SecurityException("HTTPS required");
        HttpURLConnection conn=(HttpURLConnection)target.openConnection();
        conn.setConnectTimeout(10000);conn.setReadTimeout(15000);
        conn.setRequestProperty("User-Agent","DeskTouchVision-Android-Update");
        conn.setRequestProperty("Accept","application/vnd.github+json");
        try{
            if(conn.getResponseCode()!=200)
                throw new IOException("GitHub returned "+conn.getResponseCode());
            try(InputStream in=conn.getInputStream();
                ByteArrayOutputStream out=new ByteArrayOutputStream()){
                byte[] buf=new byte[4096];int n;
                while((n=in.read(buf))!=-1){
                    if(out.size()+n>maxBytes)throw new IOException("Update metadata too large");
                    out.write(buf,0,n);
                }
                return out.toString(StandardCharsets.UTF_8.name());
            }
        } finally {conn.disconnect();}
    }
    private static Release fetchRelease() throws Exception{
        JSONObject o=new JSONObject(fetchText(API,200_000));
        String tag=o.getString("tag_name");
        if(!tag.matches("v[0-9]+\\.[0-9]+\\.[0-9]+"))
            throw new SecurityException("Unexpected official release version");
        String version=tag.substring(1),apk=null,hash=null;
        JSONArray assets=o.getJSONArray("assets");
        for(int i=0;i<assets.length();i++){
            JSONObject item=assets.getJSONObject(i);
            String name=item.getString("name");
            String url=item.getString("browser_download_url");
            if(!url.startsWith(PREFIX+tag+"/"))continue;
            if(name.equals(APK)&&url.equals(PREFIX+tag+"/"+APK))apk=url;
            if(name.equals(HASH)&&url.equals(PREFIX+tag+"/"+HASH))hash=url;
        }
        if(apk==null||hash==null)
            throw new IOException("Android APK is not yet attached to this release");
        return new Release(version,apk,hash);
    }
    private static File verifiedPath(Context c,String version){
        return new File(new File(c.getFilesDir(),"updates"),
            "DeskTouchVision-"+version+".apk");
    }
    private static File downloadVerified(Context context,Release release)
        throws Exception {
        File dest=verifiedPath(context,release.version);
        String checksum=fetchText(release.shaUrl,512).trim().split("\\s+")[0];
        if(!checksum.matches("[0-9A-Fa-f]{64}"))
            throw new IOException("No valid SHA-256 from official release");
        if(dest.isFile()&&checksum.equalsIgnoreCase(sha256(dest)))return dest;
        File dir=dest.getParentFile();
        if(!dir.isDirectory()&&!dir.mkdirs())throw new IOException("Cannot make update cache");
        File part=new File(dir,"pending.apk.part");
        if(part.exists()&&!part.delete())throw new IOException("Cannot clear interrupted download");
        URL url=new URL(release.apk);
        HttpURLConnection conn=(HttpURLConnection)url.openConnection();
        conn.setConnectTimeout(12000);conn.setReadTimeout(20000);
        conn.setRequestProperty("User-Agent","DeskTouchVision-Android-Update");
        conn.setInstanceFollowRedirects(true);
        try{
            if(conn.getResponseCode()!=200)
                throw new IOException("APK download failed: "+conn.getResponseCode());
            long size=conn.getContentLengthLong();
            if(size>150L*1024*1024)
                throw new IOException("APK exceeds download limit");
            long transferred=0;
            try(InputStream in=conn.getInputStream();
                FileOutputStream out=new FileOutputStream(part)){
                byte[] buffer=new byte[16384];int n;
                while((n=in.read(buffer))!=-1){
                    transferred+=n;
                    if(transferred>150L*1024*1024)throw new IOException("APK too large");
                    out.write(buffer,0,n);
                }
                out.getFD().sync();
            }
            if(transferred<10000 || !checksum.equalsIgnoreCase(sha256(part)))
                throw new SecurityException("APK SHA-256 mismatch; download rejected");
            if(dest.exists()&&!dest.delete())throw new IOException("Cannot replace APK cache");
            if(!part.renameTo(dest))throw new IOException("Cannot commit verified APK");
            return dest;
        } finally {
            conn.disconnect();
            if(part.exists())part.delete();
        }
    }
    private static String sha256(File file) throws Exception{
        MessageDigest digest=MessageDigest.getInstance("SHA-256");
        try(FileInputStream input=new FileInputStream(file)){
            byte[] buf=new byte[16384];int n;
            while((n=input.read(buf))!=-1)digest.update(buf,0,n);
        }
        StringBuilder s=new StringBuilder();
        for(byte b:digest.digest())s.append(String.format(Locale.ROOT,"%02x",b&255));
        return s.toString();
    }

    static void checkForUpdates(Activity activity,boolean interactive){
        if(busy)return;
        busy=true;
        WORK.execute(()->{
            try{
                Release release=fetchRelease();
                latest=release;
                int comparison=compareVersions(release.version,installedVersion(activity));
                if(comparison>0){
                    if(interactive)activity.runOnUiThread(()->new AlertDialog.Builder(activity)
                        .setTitle("DeskTouch update "+release.version)
                        .setMessage("An official Android update is available. The APK is SHA-256 verified. "+
                            "Android will require your approval to install.")
                        .setNegativeButton("Later",null)
                        .setPositiveButton("Update existing app",(d,w)->installAvailableUpdate(activity))
                        .show());
                    else {
                        File cached=downloadVerified(activity.getApplicationContext(),release);
                        notifyAvailable(activity.getApplicationContext(),release.version);
                    }
                }else if(interactive)
                    activity.runOnUiThread(()->new AlertDialog.Builder(activity)
                        .setTitle("DeskTouch is up to date")
                        .setMessage("Installed Android version: "+installedVersion(activity))
                        .setPositiveButton("OK",null).show());
            }catch(Exception e){
                if(interactive)activity.runOnUiThread(()->new AlertDialog.Builder(activity)
                    .setTitle("Could not check updates").setMessage(e.getMessage())
                    .setPositiveButton("OK",null).show());
                else Log.w("DeskTouch","Update check deferred",e);
            }finally{busy=false;}
        });
    }
    static void checkBackground(Context context){
        long now=System.currentTimeMillis();
        android.content.SharedPreferences prefs=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE);
        if(now-prefs.getLong("lastCheck",0)<6L*60*60*1000) return;
        if(busy)return;
        busy=true;
        WORK.execute(()->{
            try{
                Release release=fetchRelease();
                latest=release;
                if(compareVersions(release.version,installedVersion(context))>0){
                    downloadVerified(context,release);
                    notifyAvailable(context,release.version);
                }
                prefs.edit().putLong("lastCheck",System.currentTimeMillis()).apply();
            }catch(Exception e){Log.w("DeskTouch","Automatic update check failed",e);}
            finally{busy=false;}
        });
    }
    private static void notifyAvailable(Context context,String version){
        NotificationManager mgr=context.getSystemService(NotificationManager.class);
        mgr.createNotificationChannel(new NotificationChannel(CHANNEL,
            "DeskTouch updates",NotificationManager.IMPORTANCE_DEFAULT));
        PendingIntent tap=PendingIntent.getActivity(context,12,
            new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        mgr.notify(37378,new Notification.Builder(context,CHANNEL)
            .setSmallIcon(R.drawable.ic_desktouch).setAutoCancel(true)
            .setContentTitle("DeskTouch Vision "+version+" ready")
            .setContentText("Tap to approve the verified Android update")
            .setContentIntent(tap).build());
    }
    static void installAvailableUpdate(Activity activity){
        WORK.execute(()->{
            try{
                Release release=latest!=null?latest:fetchRelease();
                latest=release;
                if(compareVersions(release.version,installedVersion(activity))<=0){
                    activity.runOnUiThread(()->new AlertDialog.Builder(activity)
                        .setTitle("No newer version")
                        .setMessage("The current DeskTouch app is already up to date.")
                        .setPositiveButton("OK",null).show());
                    return;
                }
                File apk=downloadVerified(activity,release);
                if(!signingMatches(activity,apk)){
                    activity.runOnUiThread(()->new AlertDialog.Builder(activity)
                        .setTitle("APK signature differs")
                        .setMessage("This preview APK is signed with a different key than the installed app. "+
                            "Android correctly refuses an in-place update. Do not bypass this security check. "+
                            "A stable publisher signing key is required for future updates; manually replacing "+
                            "the old preview would lose local trusted-device data.")
                        .setPositiveButton("OK",null).show());
                    return;
                }
                // A legitimate update keeps the same app ID, signing
                // identity and Android private storage. Snapshot only the
                // user's permission state; never uninstall or clear data.
                PermissionContinuity.checkpointBeforeInstaller(activity);
                activity.runOnUiThread(()->openInstaller(activity,apk));
            }catch(Exception e){
                activity.runOnUiThread(()->new AlertDialog.Builder(activity)
                    .setTitle("Update not installed").setMessage(e.getMessage())
                    .setPositiveButton("OK",null).show());
            }
        });
    }

    private static boolean signingMatches(Context context,File apk) throws Exception {
        PackageManager pm=context.getPackageManager();
        if(Build.VERSION.SDK_INT>=28){
            PackageInfo current=pm.getPackageInfo(context.getPackageName(),
                PackageManager.GET_SIGNING_CERTIFICATES);
            PackageInfo incoming=pm.getPackageArchiveInfo(apk.getAbsolutePath(),
                PackageManager.GET_SIGNING_CERTIFICATES);
            if(incoming==null||incoming.signingInfo==null||current.signingInfo==null)
                throw new SecurityException("APK signing details unavailable");
            if(!context.getPackageName().equals(incoming.packageName))
                throw new SecurityException("APK package name differs");
            android.content.pm.Signature[] a=current.signingInfo.getApkContentsSigners();
            android.content.pm.Signature[] b=incoming.signingInfo.getApkContentsSigners();
            return Arrays.equals(a,b);
        }
        PackageInfo current=pm.getPackageInfo(context.getPackageName(),PackageManager.GET_SIGNATURES);
        PackageInfo incoming=pm.getPackageArchiveInfo(apk.getAbsolutePath(),PackageManager.GET_SIGNATURES);
        if(incoming==null||!context.getPackageName().equals(incoming.packageName))
            throw new SecurityException("APK package identity differs");
        return Arrays.equals(current.signatures,incoming.signatures);
    }

    private static void openInstaller(Activity activity,File apk){
        if(Build.VERSION.SDK_INT>=26 &&
            !activity.getPackageManager().canRequestPackageInstalls()){
            new AlertDialog.Builder(activity)
                .setTitle("Allow Android package installation")
                .setMessage("Android requires permission for DeskTouch to request APK updates. "+
                    "Allow it in Android Settings, then tap 'Update existing application' again.")
                .setNegativeButton("Cancel",null)
                .setPositiveButton("Android settings",(d,w)->activity.startActivity(
                    new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                        Uri.parse("package:"+activity.getPackageName()))))
                .show();
            return;
        }
        Uri uri=FileProvider.getUriForFile(activity,
            activity.getPackageName()+".fileprovider",apk);
        Intent intent=new Intent(Intent.ACTION_VIEW);
        intent.setDataAndType(uri,"application/vnd.android.package-archive");
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        activity.startActivity(intent); // Android OS shows its own install confirmation.
    }
}
