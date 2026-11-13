package com.desktouch.vision;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.SecureRandom;
import java.util.HashSet;
import java.util.Set;
import java.util.Locale;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Windows trust token is encrypted with an Android Keystore key. No IP stored. */
final class TrustedWindows {
    private static final String ALIAS="DeskTouchVision.TrustedWindows.v1";
    private final SharedPreferences prefs;
    TrustedWindows(Context context) {
        prefs=context.getSharedPreferences("trusted_windows",Context.MODE_PRIVATE);
    }
    private SecretKey key() throws Exception {
        KeyStore ks=KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if(ks.containsAlias(ALIAS)) return (SecretKey)ks.getKey(ALIAS,null);
        KeyGenerator gen=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
        gen.init(new KeyGenParameterSpec.Builder(ALIAS,
            KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).build());
        return gen.generateKey();
    }
    synchronized void save(String fingerprint,String token) throws Exception {
        if(fingerprint==null || token==null)
            throw new SecurityException("Invalid trusted device identity");
        fingerprint=fingerprint.toUpperCase(Locale.ROOT);
        if(!fingerprint.matches("[A-Fa-f0-9]{64}") || !token.matches("[A-Fa-f0-9]{64}"))
            throw new SecurityException("Invalid trusted device identity");
        byte[] iv=new byte[12];
        new SecureRandom().nextBytes(iv);
        Cipher aes=Cipher.getInstance("AES/GCM/NoPadding");
        aes.init(Cipher.ENCRYPT_MODE,key(),new GCMParameterSpec(128,iv));
        aes.updateAAD(fingerprint.getBytes(StandardCharsets.US_ASCII));
        byte[] sealed=aes.doFinal(token.getBytes(StandardCharsets.US_ASCII));
        byte[] combined=new byte[iv.length+sealed.length];
        System.arraycopy(iv,0,combined,0,iv.length);
        System.arraycopy(sealed,0,combined,iv.length,sealed.length);
        if(!prefs.edit().putString(fingerprint,Base64.encodeToString(combined,Base64.NO_WRAP)).commit())
            throw new IllegalStateException("Could not persist device trust");
    }
    synchronized String get(String fingerprint) {
        if(fingerprint==null) return null;
        String normalized=fingerprint.toUpperCase(Locale.ROOT);
        String encoded=prefs.getString(normalized,null);
        if(encoded==null) encoded=prefs.getString(fingerprint,null);
        if(encoded==null) return null;
        try{
            byte[] combined=Base64.decode(encoded,Base64.NO_WRAP);
            if(combined.length<29) throw new SecurityException("Corrupt trusted device record");
            byte[] iv=new byte[12];
            System.arraycopy(combined,0,iv,0,12);
            Cipher aes=Cipher.getInstance("AES/GCM/NoPadding");
            aes.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,iv));
            aes.updateAAD(normalized.getBytes(StandardCharsets.US_ASCII));
            return new String(aes.doFinal(combined,12,combined.length-12),StandardCharsets.US_ASCII);
        }catch(Exception ignored){
            forget(fingerprint);
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
        prefs.edit().remove(normalized).remove(fingerprint).commit();
    }
    synchronized void clearAll() {
        prefs.edit().clear().commit();
    }
}
