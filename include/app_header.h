#ifndef APP_HEADER_H
#define APP_HEADER_H

#include <stdint.h>

#define APP_HEADER_MAGIC    0x41505001U  /* "APP\x01" */

typedef struct __attribute__((packed)) {
    uint32_t magic;         /* Must be APP_HEADER_MAGIC */
    uint32_t version;       /* (major<<16)|(minor<<8)|patch */
    uint32_t size;          /* Application binary size (after header) */
    uint32_t crc32;         /* CRC32 of application data */
    uint32_t entry_point;   /* Entry point address (informational) */
    uint8_t  reserved[236]; /* Pad to 256 bytes total */
} app_header_t;

_Static_assert(sizeof(app_header_t) == 256, "app_header_t must be exactly 256 bytes");

/* Declared in app_header.c, placed in .app_header section */
extern const app_header_t app_header;

#endif /* APP_HEADER_H */
