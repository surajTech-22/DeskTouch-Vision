package com.desktouch.vision;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

/**
 * Pure package-name detector for payment, UPI, wallet, and mobile banking apps.
 * Uses only the active package name supplied by Android window transition events
 * so DeskTouch can turn Accessibility OFF without inspecting screen contents
 * or querying installed package inventories.
 */
final class PaymentAppDetector {
    private static final Set<String> EXACT_PACKAGES = Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
        // UPI & digital wallets
        "com.google.android.apps.nbu.paisa.user",
        "com.google.android.apps.nbu.paisa.merchant",
        "com.google.android.apps.walletnfcrel",
        "com.phonepe.app",
        "com.phonepe.app.business",
        "net.one97.paytm",
        "com.paytm.business",
        "com.paytmmall",
        "net.one97.paytm.money",
        "com.paytmmoney",
        "in.org.npci.upiapp",
        "com.dreamplug.androidapp",
        "com.mobikwik_new",
        "com.freecharge.android",
        "com.naviapp",
        "indwin.c3.shareapp",
        "money.jupiter",
        "com.epifi.paisa",
        "money.super.payments",
        "com.fampay.in",
        "com.bharatpe.app",
        "com.postpe.app",
        "com.paypal.android.p2pmobile",
        "com.samsung.android.spay",
        "com.samsung.android.samsungwallet",
        // Major banks & investing apps that block enabled Accessibility services
        "com.sbi.lotusintouch",
        "com.sbi.sbifreedomplus",
        "com.sbi.upi",
        "com.snapwork.hdfc",
        "com.hdfc.mobilebanking",
        "com.enstage.wibmo.hdfc",
        "com.csam.icici.bank.imobile",
        "com.icicibank.imobile",
        "com.icici.pockets",
        "com.axis.mobile",
        "com.axis.netsecure",
        "com.msf.kbank.mobile",
        "com.kotak811mobilebankingapp",
        "com.kotak.bank.android",
        "com.kotak.neo",
        "com.bankofbaroda.mconnect",
        "com.bankofbaroda.upi",
        "com.canarabank.mobility",
        "com.version1",
        "com.idfcfirstbank.optimus",
        "com.fss.indus",
        "com.indusind.indie",
        "com.fedmobile",
        "com.unionbank.ecommerce.mobile.android",
        "com.lcode.ucoupi",
        "com.rblbank.mobank",
        "com.enstage.wibmo.yes",
        "com.yesbank.iris",
        "com.infrasofttech.centralbank",
        "com.nextbillion.groww",
        "com.zerodha.kite3",
        "com.zerodha.coin",
        "in.upstox.app",
        "com.msf.angelmobile"
    )));

    private static final String[] IGNORED_PREFIXES = new String[] {
        "com.desktouch.",
        "com.android.",
        "android",
        "com.google.android.gms",
        "com.google.android.gsf",
        "com.google.android.inputmethod.",
        "com.google.android.permissioncontroller",
        "com.miui.",
        "com.xiaomi.",
        "com.whatsapp",
        "com.instagram.",
        "com.facebook.",
        "com.youtube",
        "com.google.android.youtube"
    };

    private static final String[] MATCH_PREFIXES = new String[] {
        "com.google.android.apps.nbu.paisa.",
        "com.phonepe.",
        "net.one97.paytm",
        "com.paytm.",
        "in.org.npci.",
        "com.enstage.wibmo.",
        "com.sbi.",
        "com.hdfc",
        "com.icici",
        "com.kotak",
        "com.axis.",
        "com.bankofbaroda.",
        "com.canarabank.",
        "com.idfcfirstbank.",
        "com.rblbank."
    };

    private static final String[] SEGMENT_TOKENS = new String[] {
        "mobilebanking",
        "netbanking",
        "payzapp",
        "paytm",
        "phonepe",
        "imobile",
        "mobank",
        "bhim",
        "yono"
    };

    private PaymentAppDetector() {}

    static boolean isPaymentOrBankingPackage(CharSequence packageSeq) {
        if (packageSeq == null) return false;
        String pkg = packageSeq.toString().trim().toLowerCase(Locale.ROOT);
        if (pkg.isEmpty() || !pkg.contains(".")) return false;
        for (String ignored : IGNORED_PREFIXES) {
            if (pkg.equals(ignored) || pkg.startsWith(ignored)) return false;
        }
        if (EXACT_PACKAGES.contains(pkg)) return true;
        for (String prefix : MATCH_PREFIXES) {
            if (pkg.startsWith(prefix)) return true;
        }
        String[] segments = pkg.split("\\.");
        for (String seg : segments) {
            if ("upi".equals(seg) || "paisa".equals(seg)) return true;
            for (String token : SEGMENT_TOKENS) {
                if (seg.contains(token)) return true;
            }
        }
        return false;
    }

    static String friendlyAppName(Context context, CharSequence packageSeq) {
        String pkg = packageSeq == null ? "" : packageSeq.toString().trim();
        if (pkg.isEmpty()) return "payment app";
        if (context != null) {
            try {
                PackageManager pm = context.getPackageManager();
                if (pm != null) {
                    ApplicationInfo info = pm.getApplicationInfo(pkg, 0);
                    CharSequence label = pm.getApplicationLabel(info);
                    if (label != null && label.length() > 0) {
                        return label.toString().trim();
                    }
                }
            } catch (RuntimeException | PackageManager.NameNotFoundException ignored) {}
        }
        int dot = pkg.lastIndexOf('.');
        String shortName = (dot >= 0 && dot + 1 < pkg.length()) ? pkg.substring(dot + 1) : pkg;
        if ("user".equals(shortName) && pkg.contains("paisa")) return "Google Pay";
        return shortName;
    }
}
