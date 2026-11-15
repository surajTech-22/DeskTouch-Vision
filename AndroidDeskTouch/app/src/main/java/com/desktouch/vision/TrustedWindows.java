package com.desktouch.vision;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.util.Log;
import java.nio.charset.StandardCharsets;
import java.security.Key;
import java.security.KeyStore;
import java.security.SecureRandom;
import java.util.HashSet;
import java.util.Set;
import java.util.Locale;
import java.util.concurrent.ConcurrentHashMap;
import javax.crypto.AEADBadTagException;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/** Windows trust token is encrypted with an Android Keystore key (with MODE_PRIVATE AES fallback). No IP stored. */
final class TrustedWindows {
    private static final String TAG="DeskTouchTrust";
    private static final String ALIAS="DeskTouchVision.TrustedWindows.v1";
    private static final String FALLBACK_PREFS="trusted_windows_k";
    private static final String FALLBACK_KEY_PREF="aes_k2";
    private static final String FALLBACK_PREFIX="k2:";
    private static final ConcurrentHashMap<String,String> CACHE=new ConcurrentHashMap<>();
    private final SharedPreferences prefs;
    private final SharedPreferences fallbackPrefs;
    TrustedWindows(Context context) {
        Context app=context.getApplicationContext();
        prefs=app.getSharedPreferences("trusted_windows",Context.MODE_PRIVATE);
        fallbackPrefs=app.getSharedPreferences(FALLBACK_PREFS,Context.MODE_PRIVATE);
    }
    private SecretKey keystoreKey(boolean resetIfBroken) throws Exception {
        KeyStore ks=KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if(!resetIfBroken && ks.containsAlias(ALIAS)) {
            Key existing=ks.getKey(ALIAS,null);
            if(existing instanceof SecretKey) return (SecretKey)existing;
        }
        if(ks.containsAlias(ALIAS)) {
            try { ks.deleteEntry(ALIAS); } catch(Exception ignored) {}
        }
        KeyGenerator gen=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
        gen.init(new KeyGenParameterSpec.Builder(ALIAS,
            KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).build());
        return gen.generateKey();
    }
    private SecretKey fallbackKey(boolean createIfMissing) {
        String existing=fallbackPrefs.getString(FALLBACK_KEY_PREF,null);
        if(existing!=null && !existing.isEmpty()) {
            try {
                byte[] raw=Base64.decode(existing,Base64.NO_WRAP);
                if(raw.length==32) return new SecretKeySpec(raw,"AES");
            } catch(IllegalArgumentException ignored) {}
        }
        if(!createIfMissing) return null;
        byte[] raw=new byte[32];
        new SecureRandom().nextBytes(raw);
        String encoded=Base64.encodeToString(raw,Base64.NO_WRAP);
        if(!fallbackPrefs.edit().putString(FALLBACK_KEY_PREF,encoded).commit()) {
            throw new IllegalStateException("Could not persist local key material");
        }
        return new SecretKeySpec(raw,"AES");
    }
    private static byte[] sealWithKey(SecretKey key,String fingerprint,String token) throws Exception {
        byte[] iv=new byte[12];
        new SecureRandom().nextBytes(iv);
        Cipher aes=Cipher.getInstance("AES/GCM/NoPadding");
        aes.init(Cipher.ENCRYPT_MODE,key,new GCMParameterSpec(128,iv));
        aes.updateAAD(fingerprint.getBytes(StandardCharsets.US_ASCII));
        byte[] sealed=aes.doFinal(token.getBytes(StandardCharsets.US_ASCII));
        byte[] combined=new byte[iv.length+sealed.length];
        System.arraycopy(iv,0,combined,0,iv.length);
        System.arraycopy(sealed,0,combined,iv.length,sealed.length);
        return combined;
    }
    private static String unsealWithKey(SecretKey key,String normalized,byte[] combined) throws Exception {
        byte[] iv=new byte[12];
        System.arraycopy(combined,0,iv,0,12);
        Cipher aes=Cipher.getInstance("AES/GCM/NoPadding");
        aes.init(Cipher.DECRYPT_MODE,key,new GCMParameterSpec(128,iv));
        aes.updateAAD(normalized.getBytes(StandardCharsets.US_ASCII));
        return new String(aes.doFinal(combined,12,combined.length-12),StandardCharsets.US_ASCII);
    }
    synchronized void save(String fingerprint,String token) throws Exception {
        if(fingerprint==null || token==null)
            throw new SecurityException("Invalid trusted device identity");
        fingerprint=fingerprint.toUpperCase(Locale.ROOT);
        if(!fingerprint.matches("[A-Fa-f0-9]{64}") || !token.matches("[A-Fa-f0-9]{64}"))
            throw new SecurityException("Invalid trusted device identity");
        String record=null;
        try {
            SecretKey key=keystoreKey(false);
            record=Base64.encodeToString(sealWithKey(key,fingerprint,token),Base64.NO_WRAP);
        } catch(Exception primaryEx) {
            Log.w(TAG,"Primary keystore seal failed, attempting key reset",primaryEx);
            try {
                SecretKey resetKey=keystoreKey(true);
                record=Base64.encodeToString(sealWithKey(resetKey,fingerprint,token),Base64.NO_WRAP);
            } catch(Exception retryEx) {
                Log.w(TAG,"Keystore unavailable on device, using MODE_PRIVATE AES fallback",retryEx);
                SecretKey fk=fallbackKey(true);
                record=FALLBACK_PREFIX+Base64.encodeToString(sealWithKey(fk,fingerprint,token),Base64.NO_WRAP);
            }
        }
        if(!prefs.edit().putString(fingerprint,record).commit())
            throw new IllegalStateException("Could not persist device trust");
        CACHE.put(fingerprint,token);
    }
    synchronized String get(String fingerprint) {
        if(fingerprint==null) return null;
        String normalized=fingerprint.toUpperCase(Locale.ROOT);
        String cached=CACHE.get(normalized);
        if(cached!=null) return cached;
        String encoded=prefs.getString(normalized,null);
        if(encoded==null) encoded=prefs.getString(fingerprint,null);
        if(encoded==null) return null;
        if(encoded.startsWith(FALLBACK_PREFIX)) {
            SecretKey fk=fallbackKey(false);
            if(fk==null) {
                Log.w(TAG,"Fallback key missing for saved trust entry");
                return null;
            }
            try {
                byte[] combined=Base64.decode(encoded.substring(FALLBACK_PREFIX.length()),Base64.NO_WRAP);
                if(combined.length<29) {
                    forget(normalized);
                    return null;
                }
                String token=unsealWithKey(fk,normalized,combined);
                CACHE.put(normalized,token);
                return token;
            } catch(IllegalArgumentException|AEADBadTagException corrupt) {
                Log.w(TAG,"Corrupt fallback trust entry removed",corrupt);
                forget(normalized);
                return null;
            } catch(Exception ex) {
                Log.w(TAG,"Transient failure reading fallback trust entry",ex);
                return null;
            }
        }
        try {
            byte[] combined=Base64.decode(encoded,Base64.NO_WRAP);
            if(combined.length<29) {
                forget(normalized);
                return null;
            }
            SecretKey key=keystoreKey(false);
            String token=unsealWithKey(key,normalized,combined);
            CACHE.put(normalized,token);
            return token;
        } catch(IllegalArgumentException|AEADBadTagException corrupt) {
            Log.w(TAG,"Unreadable keystore trust record removed",corrupt);
            forget(normalized);
            return null;
        } catch(Exception ex) {
            // Transient keystore/lockscreen errors must never wipe saved user trust.
            Log.w(TAG,"Keystore read temporarily failed; preserving saved entry",ex);
            return null;
        }
    }
    synchronized Set<String> fingerprints() {
        Set<String> identities=new HashSet<>();
        for(String stored:prefs.getAll().keySet())
            if(stored.matches("[A-Fa-f0-9]{64}"))
                identities.add(stored.toUpperCase(Locale.ROOT));
        return identities;
    }
    synchronized void forget(String fingerprint) {
        if(fingerprint==null) return;
        String normalized=fingerprint.toUpperCase(Locale.ROOT);
        CACHE.remove(normalized);
        prefs.edit().remove(normalized).remove(fingerprint).commit();
    }
    synchronized void clearAll() {
        CACHE.clear();
        prefs.edit().clear().commit();
    }
}
