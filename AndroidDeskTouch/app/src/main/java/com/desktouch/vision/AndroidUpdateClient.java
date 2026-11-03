package com.desktouch.vision;

import android.app.*;
import android.content.*;
import android.content.pm.*;
import android.net.*;
import android.os.*;
import android.provider.Settings;
import androidx.core.content.FileProvider;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import java.util.function.Consumer;

/**
 * GitHub-release updater for the same installed Android application.
 * Checks the latest vX.Y.Z, downloads official APK + .sha256 with HTTPS,
 * verifies SHA-256 AND signing certificate before requesting Android's own
 * Package Installer. Never silently installs or bypasses Android approval.
 */
final class AndroidUpdateClient {
    private static final String API="https://api.github.com/repos/surajTech-22/DeskTouch-Vision/releases/latest";
    private static final String REPO="https://github.com/surajTech-22/DeskTouch-Vision/releases/download/";
    private static final String APK="DeskTouchVision-android-preview.apk";
    private static final String SHA=APK+".sha256";
    private static final String CHANNEL="desktouch_app_updates";
    private static final String PREFS="desktouch_mobile_update";
    private static final String VERSION="verified_version";
    private static final String HASH="verified_sha256";
    static final String EXTRA_INSTALL="desktouch.install_verified_update";
    private final Context app;
    AndroidUpdateClient(Context context){app=context.getApplicationContext();}

    private static void log(Consumer<String> status,String message){
        if(status!=null)status.accept(message);
    }
    private static int[] parseVersion(String value){
        if(value==null)throw new IllegalArgumentException("Missing version");
        String v=value.trim().replaceFirst("^v","");
        if(!v.matches("[0-9]+\\.[0-9]+\\.[0-9]+"))
            throw new IllegalArgumentException("Invalid release version");
        String[] bits=v.split("\\.");
        return new int[]{Integer.parseInt(bits[0]),Integer.parseInt(bits[1]),Integer.parseInt(bits[2])};
    }
    static boolean isNewer(String published,String installed){
        int[] a=parseVersion(published),b=parseVersion(installed);
        for(int i=0;i<3;i++){if(a[i]!=b[i])return a[i]>b[i];}
        return false;
    }
    private static byte[] download(String url,int max) throws Exception{
        URL source=new URL(url);
        if(!"https".equals(source.getProtocol()))throw new SecurityException("HTTPS required");
        HttpURLConnection con=(HttpURLConnection)source.openConnection();
        con.setConnectTimeout(12000);con.setReadTimeout(25000);
        con.setRequestProperty("User-Agent","DeskTouchVision-Android/"+BuildConfig.VERSION_NAME);
        con.setInstanceFollowRedirects(true);
        try{
            if(con.getResponseCode()!=200)throw new IOException("GitHub HTTP "+con.getResponseCode());
            try(InputStream in=con.getInputStream();ByteArrayOutputStream out=new ByteArrayOutputStream()){
                byte[] b=new byte[8192];int n;
                while((n=in.read(b))!=-1){
                    if(out.size()+n>max)throw new IOException("Update exceeds expected size");
                    out.write(b,0,n);
                }
                return out.toByteArray();
            }
        }finally{con.disconnect();}
    }
    private static String hex(byte[] digest){
        StringBuilder s=new StringBuilder(digest.length*2);
        for(byte b:digest)s.append(String.format(Locale.ROOT,"%02x",b&255));
        return s.toString();
    }
    private static String sha256(File file) throws Exception {
        MessageDigest digest=MessageDigest.getInstance("SHA-256");
        try(InputStream input=new FileInputStream(file)){
            byte[] buf=new byte[32768];int n;
            while((n=input.read(buf))!=-1)digest.update(buf,0,n);
        }
        return hex(digest.digest());
    }
    private static String signedIdentity(PackageInfo info){
        if(info==null)return null;
        android.content.pm.Signature[] signatures;
        if(Build.VERSION.SDK_INT>=28){
            if(info.signingInfo==null)return null;
            signatures=info.signingInfo.hasMultipleSigners()?
                info.signingInfo.getApkContentsSigners():
                info.signingInfo.getSigningCertificateHistory();
        }else signatures=info.signatures;
        if(signatures==null||signatures.length==0)return null;
        try{
            ArrayList<String> hashes=new ArrayList<>();
            for(android.content.pm.Signature s:signatures)
                hashes.add(hex(MessageDigest.getInstance("SHA-256").digest(s.toByteArray())));
            Collections.sort(hashes);
            return String.join(",",hashes);
        }catch(Exception e){return null;}
    }
    private static boolean compatibleSigning(Context context,File apk){
        try{
            PackageManager pm=context.getPackageManager();
            int flags=Build.VERSION.SDK_INT>=28?
                PackageManager.GET_SIGNING_CERTIFICATES:PackageManager.GET_SIGNATURES;
            PackageInfo current=pm.getPackageInfo(context.getPackageName(),flags);
            PackageInfo future=pm.getPackageArchiveInfo(apk.getAbsolutePath(),flags);
            if(future==null||!context.getPackageName().equals(future.packageName))return false;
            if(Build.VERSION.SDK_INT>=28 && future.getLongVersionCode()<=current.getLongVersionCode())
                return false;
            return Objects.equals(signedIdentity(current),signedIdentity(future)) &&
                signedIdentity(current)!=null;
        }catch(Exception e){return false;}
    }
    private File file(){return new File(app.getCacheDir(),"desktouch-verified-update.apk");}
    private boolean verifiedCached() {
        try{
            File target=file();
            String hash=app.getSharedPreferences(PREFS,Context.MODE_PRIVATE)
                .getString(HASH,null);
            return target.isFile() && hash!=null && hash.matches("[a-f0-9]{64}") &&
                hash.equalsIgnoreCase(sha256(target));
        }catch(Exception e){return false;}
    }
    boolean updateReady(){return verifiedCached();}
    boolean canUpdateInstalled(){return verifiedCached()&&compatibleSigning(app,file());}

    void check(boolean automatic,Consumer<String> callback) {
        try{
            if(automatic){
                android.net.ConnectivityManager cm=(android.net.ConnectivityManager)
                    app.getSystemService(Context.CONNECTIVITY_SERVICE);
                if(cm!=null&&cm.isActiveNetworkMetered())return;
            }
            JSONObject release=new JSONObject(new String(download(API,1000000),StandardCharsets.UTF_8));
            String version=release.getString("tag_name");
            if(!isNewer(version,BuildConfig.VERSION_NAME)){
                log(callback,"DeskTouch Android "+BuildConfig.VERSION_NAME+" is current.");
                return;
            }
            if(!version.matches("v[0-9]+\\.[0-9]+\\.[0-9]+"))
                throw new SecurityException("Untrusted release tag");
            String apkUrl=null,shaUrl=null;
            JSONArray assets=release.getJSONArray("assets");
            for(int i=0;i<assets.length();i++){
                JSONObject a=assets.getJSONObject(i);
                String n=a.optString("name");
                if(APK.equals(n))apkUrl=a.optString("browser_download_url");
                if(SHA.equals(n))shaUrl=a.optString("browser_download_url");
            }
            String prefix=REPO+version+"/";
            if(apkUrl==null||shaUrl==null||!apkUrl.equals(prefix+APK)||
               !shaUrl.equals(prefix+SHA))
                throw new IOException("Release does not contain the official verified Android APK");
            String hashText=new String(download(shaUrl,4096),StandardCharsets.UTF_8).trim();
            if(!hashText.matches("(?i)^[a-f0-9]{64}\\s+\\*?"+APK+"$"))
                throw new SecurityException("Invalid published APK SHA-256");
            String expected=hashText.substring(0,64).toLowerCase(Locale.ROOT);
            File temp=new File(app.getCacheDir(),"desktouch-update.part");
            File target=file();
            byte[] data=download(apkUrl,200_000_000);
            try(FileOutputStream stream=new FileOutputStream(temp)){
                stream.write(data);stream.getFD().sync();
            }
            if(!expected.equalsIgnoreCase(sha256(temp))){
                temp.delete();throw new SecurityException("Update SHA-256 mismatch");
            }
            if(!temp.renameTo(target)){
                temp.delete();throw new IOException("Cannot finalize verified APK");
            }
            app.getSharedPreferences(PREFS,Context.MODE_PRIVATE).edit()
                .putString(VERSION,version).putString(HASH,expected).apply();
            if(!canUpdateInstalled()){
                log(callback,"New "+version+" downloaded and verified, but its signing certificate "+
                    "does not match this installed app. Android will not install it as an update. "+
                    "A stable Android release-signing key is required; app data was preserved.");
                return;
            }
            log(callback,"Android "+version+" downloaded and verified. Select Update existing application.");
            notifyReady(version);
        }catch(Exception e){
            log(callback,"Update check: "+e.getMessage());
        }
    }
    private void notifyReady(String version){
        NotificationManager manager=app.getSystemService(NotificationManager.class);
        if(manager==null)return;
        manager.createNotificationChannel(new NotificationChannel(CHANNEL,
            "DeskTouch app updates",NotificationManager.IMPORTANCE_DEFAULT));
        Intent intent=new Intent(app,MainActivity.class)
            .putExtra(EXTRA_INSTALL,true)
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent action=PendingIntent.getActivity(app,4600,intent,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification note=new Notification.Builder(app,CHANNEL)
            .setSmallIcon(R.drawable.ic_desktouch)
            .setContentTitle("DeskTouch "+version+" update ready")
            .setContentText("Tap to review and install the verified update.")
            .setContentIntent(action).setAutoCancel(true).build();
        manager.notify(4600,note);
    }
    void installExisting(Activity activity,Consumer<String> status){
        if(!verifiedCached()){
            log(status,"No verified APK downloaded. Choose Get updates first.");
            return;
        }
        if(!compatibleSigning(app,file())){
            log(status,"This APK cannot replace the installed application: Android "+
                "requires the same package name and signing key, and a newer version. "+
                "Your existing app and trusted settings have not been changed.");
            return;
        }
        if(Build.VERSION.SDK_INT>=26 &&
            !app.getPackageManager().canRequestPackageInstalls()){
            log(status,"Android permission needed: allow installation from DeskTouch, "+
                "then tap Update existing application again.");
            Intent options=new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                Uri.parse("package:"+app.getPackageName()));
            activity.startActivity(options);
            return;
        }
        try{
            Uri location=FileProvider.getUriForFile(activity,
                app.getPackageName()+".updates",file());
            Intent installer=new Intent(Intent.ACTION_VIEW);
            installer.setDataAndType(location,"application/vnd.android.package-archive");
            installer.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            activity.startActivity(installer); // Android requires user approval.
            log(status,"Review the Android system installation prompt to update DeskTouch.");
        }catch(Exception e){log(status,"System installer unavailable: "+e.getMessage());}
    }
}
