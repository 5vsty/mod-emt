#!/bin/bash
if [ -f /app/userConfig/userConfig.yaml ]; then
    cp /app/userConfig/userConfig.yaml /app/config/
fi

cd /app/

/app/FSDEV.MOD.Emt