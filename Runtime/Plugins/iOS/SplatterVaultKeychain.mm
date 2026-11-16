// SplatterVault Unity SDK: iOS Keychain storage for the player's refresh token and guest secret
// (Runtime/Players/IosKeychainSecureStore.cs). Generic-password items, accessible after the first unlock,
// this device only (never synced to iCloud, never restored to another device). CoreFoundation only, so it
// builds with or without ARC. Links Security.framework (see the .meta).
#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <stdlib.h>
#include <string.h>

static CFMutableDictionaryRef SVQuery(const char *service, const char *account)
{
    CFMutableDictionaryRef query = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks,
                                                             &kCFTypeDictionaryValueCallBacks);
    CFStringRef svc = CFStringCreateWithCString(kCFAllocatorDefault, service ? service : "", kCFStringEncodingUTF8);
    CFStringRef acc = CFStringCreateWithCString(kCFAllocatorDefault, account ? account : "", kCFStringEncodingUTF8);
    CFDictionarySetValue(query, kSecClass, kSecClassGenericPassword);
    CFDictionarySetValue(query, kSecAttrService, svc);
    CFDictionarySetValue(query, kSecAttrAccount, acc);
    CFRelease(svc);
    CFRelease(acc);
    return query;
}

extern "C" {

int sv_keychain_set(const char *service, const char *account, const unsigned char *value, int length)
{
    if (value == NULL || length < 0) return (int)errSecParam;
    CFDataRef data = CFDataCreate(kCFAllocatorDefault, (const UInt8 *)value, (CFIndex)length);
    CFMutableDictionaryRef query = SVQuery(service, account);
    CFMutableDictionaryRef update = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks,
                                                              &kCFTypeDictionaryValueCallBacks);
    CFDictionarySetValue(update, kSecValueData, data);
    CFDictionarySetValue(update, kSecAttrAccessible, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
    OSStatus status = SecItemUpdate(query, update);
    if (status == errSecItemNotFound)
    {
        CFDictionarySetValue(query, kSecValueData, data);
        CFDictionarySetValue(query, kSecAttrAccessible, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
        status = SecItemAdd(query, NULL);
    }
    CFRelease(update);
    CFRelease(query);
    CFRelease(data);
    return (int)status;
}

int sv_keychain_get(const char *service, const char *account, char **value, int *length)
{
    *value = NULL;
    *length = 0;
    CFMutableDictionaryRef query = SVQuery(service, account);
    CFDictionarySetValue(query, kSecReturnData, kCFBooleanTrue);
    CFDictionarySetValue(query, kSecMatchLimit, kSecMatchLimitOne);
    CFTypeRef result = NULL;
    OSStatus status = SecItemCopyMatching(query, &result);
    CFRelease(query);
    if (status == errSecSuccess && result != NULL)
    {
        CFDataRef data = (CFDataRef)result;
        CFIndex dataLength = CFDataGetLength(data);
        char *buffer = (char *)malloc((size_t)dataLength + 1);
        if (buffer == NULL)
        {
            CFRelease(result);
            return (int)errSecAllocate;
        }
        memcpy(buffer, CFDataGetBytePtr(data), (size_t)dataLength);
        buffer[dataLength] = '\0';
        *value = buffer;
        *length = (int)dataLength;
    }
    if (result != NULL) CFRelease(result);
    return (int)status;
}

int sv_keychain_delete(const char *service, const char *account)
{
    CFMutableDictionaryRef query = SVQuery(service, account);
    OSStatus status = SecItemDelete(query);
    CFRelease(query);
    return (int)status;
}

void sv_keychain_free(char *value)
{
    free(value);
}

}
