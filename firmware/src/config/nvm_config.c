#include "config/nvm_config.h"
#include "hal/hal_flash_nvm.h"
#include "util/crc32.h"
#include <string.h>

/* ---- Internal Helpers ---- */

static uint32_t compute_config_crc(const nvm_config_t *cfg)
{
    /* CRC over everything except the trailing crc32 field */
    size_t crc_len = sizeof(nvm_config_t) - sizeof(uint32_t);
    return crc32_compute(cfg, crc_len);
}

static uint32_t compute_meta_crc(const nvm_meta_t *meta)
{
    size_t crc_len = sizeof(nvm_meta_t) - sizeof(uint32_t);
    return crc32_compute(meta, crc_len);
}

static bool validate_config_slot(const nvm_config_t *cfg)
{
    if (cfg->magic != NVM_CONFIG_MAGIC) return false;
    if (cfg->version != NVM_CONFIG_VERSION) return false;
    if (cfg->size != sizeof(nvm_config_t)) return false;
    return (cfg->crc32 == compute_config_crc(cfg));
}

static bool read_meta(nvm_meta_t *meta)
{
    if (!hal_nvm_read(NVM_META_OFFSET, meta, sizeof(nvm_meta_t)))
        return false;
    if (meta->magic != NVM_META_MAGIC) return false;
    return (meta->crc32 == compute_meta_crc(meta));
}

static bool write_meta(const nvm_meta_t *meta)
{
    if (!hal_nvm_erase_sector(NVM_META_OFFSET)) return false;
    return hal_nvm_write(NVM_META_OFFSET, meta, sizeof(nvm_meta_t));
}

static uint32_t slot_offset(uint8_t slot)
{
    return (slot == 0) ? NVM_SLOT_A_OFFSET : NVM_SLOT_B_OFFSET;
}

static bool read_config_slot(uint8_t slot, nvm_config_t *cfg)
{
    return hal_nvm_read(slot_offset(slot), cfg, sizeof(nvm_config_t));
}

static bool write_config_slot(uint8_t slot, const nvm_config_t *cfg)
{
    uint32_t offset = slot_offset(slot);
    if (!hal_nvm_erase_sector(offset)) return false;
    return hal_nvm_write(offset, cfg, sizeof(nvm_config_t));
}

/* ---- Public API ---- */

void nvm_config_defaults(nvm_config_t *cfg)
{
    memset(cfg, 0, sizeof(nvm_config_t));

    cfg->magic   = NVM_CONFIG_MAGIC;
    cfg->version = NVM_CONFIG_VERSION;
    cfg->size    = sizeof(nvm_config_t);
    cfg->write_count = 0;

    /* CAN defaults */
    cfg->can[0].bitrate     = CAN_DEFAULT_BITRATE;
    cfg->can[0].termination = 1;  /* CAN1 termination on by default */
    cfg->can[0].enabled     = 1;  /* CAN1 always on */

    cfg->can[1].bitrate     = CAN_DEFAULT_BITRATE;
    cfg->can[1].termination = 0;
    cfg->can[1].enabled     = 0;  /* CAN2 disabled by default */

    /* LIN defaults */
    for (int ch = 0; ch < LIN_CHANNEL_COUNT; ch++) {
        cfg->lin[ch].enabled  = 0;
        cfg->lin[ch].mode     = LIN_MODE_DISABLED;
        cfg->lin[ch].baudrate = LIN_DEFAULT_BAUDRATE;
        cfg->lin[ch].schedule.count = 0;
    }

    /* No routing rules */
    cfg->routing_rule_count = 0;

    /* Diagnostics defaults */
    cfg->diag.can_id          = DIAG_DEFAULT_CAN_ID;
    cfg->diag.interval_ms     = DIAG_DEFAULT_INTERVAL_MS;
    cfg->diag.bus             = 0;  /* CAN1 */
    cfg->diag.enabled         = 1;
    cfg->diag.can_watchdog_ms = 0;  /* disabled by default */
    cfg->diag.lin_watchdog_ms = 0;  /* disabled by default */

    /* Profiles disabled */
    cfg->profiles.wda_enabled    = 0;
    cfg->profiles.wda_channel    = 0;
    cfg->profiles.cwa400_enabled = 0;
    cfg->profiles.cwa400_channel = 0;

    /* Compute CRC */
    cfg->crc32 = compute_config_crc(cfg);
}

bool nvm_config_validate(const nvm_config_t *cfg)
{
    return validate_config_slot(cfg);
}

bool nvm_config_load(nvm_config_t *cfg)
{
    nvm_meta_t meta;
    static nvm_config_t tmp;  /* static to avoid ~3 KB stack allocation */

    /* Try to read meta sector */
    if (read_meta(&meta)) {
        /* Try active slot */
        if (read_config_slot(meta.active_slot, &tmp) && validate_config_slot(&tmp)) {
            memcpy(cfg, &tmp, sizeof(nvm_config_t));
            return true;
        }
        /* Fallback: try other slot */
        uint8_t other = meta.active_slot ? 0 : 1;
        if (read_config_slot(other, &tmp) && validate_config_slot(&tmp)) {
            memcpy(cfg, &tmp, sizeof(nvm_config_t));
            return true;
        }
    } else {
        /* Meta invalid — try both slots */
        if (read_config_slot(0, &tmp) && validate_config_slot(&tmp)) {
            memcpy(cfg, &tmp, sizeof(nvm_config_t));
            return true;
        }
        if (read_config_slot(1, &tmp) && validate_config_slot(&tmp)) {
            memcpy(cfg, &tmp, sizeof(nvm_config_t));
            return true;
        }
    }

    /* Both invalid — load defaults into RAM only.
     * Do NOT write to NVM during boot: flash addressing must be
     * verified first, and the boot path should never risk a flash fault.
     * NVM is written explicitly via the SAVE command. */
    nvm_config_defaults(cfg);
    return false;
}

bool nvm_config_save(const nvm_config_t *cfg)
{
    nvm_meta_t meta;
    uint8_t target_slot;

    /* Read current meta to determine inactive slot */
    if (read_meta(&meta)) {
        target_slot = meta.active_slot ? 0 : 1;
    } else {
        /* Meta invalid — start fresh with slot A */
        target_slot = 0;
        memset(&meta, 0, sizeof(meta));
        meta.magic = NVM_META_MAGIC;
    }

    /* Prepare config copy with updated CRC and write_count.
     * Static to avoid ~3 KB stack allocation — safe because this
     * function is only called from a single task (not re-entrant). */
    static nvm_config_t save_cfg;
    memcpy(&save_cfg, cfg, sizeof(nvm_config_t));
    save_cfg.write_count = meta.write_count + 1;
    save_cfg.crc32 = compute_config_crc(&save_cfg);

    /* Write config to inactive slot */
    if (!write_config_slot(target_slot, &save_cfg))
        return false;

    /* Update meta to point to new slot */
    meta.active_slot = target_slot;
    meta.write_count = save_cfg.write_count;
    meta.crc32       = compute_meta_crc(&meta);

    return write_meta(&meta);
}

uint32_t nvm_config_get_write_count(void)
{
    nvm_meta_t meta;
    if (read_meta(&meta)) {
        return meta.write_count;
    }
    return 0;
}
