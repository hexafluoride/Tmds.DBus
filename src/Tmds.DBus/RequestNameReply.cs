﻿// Copyright 2016 Tom Deseyn <tom.deseyn@gmail.com>
// This software is made available under the MIT License
// See COPYING for details

namespace Tmds.DBus
{
    public enum RequestNameReply : uint
    {
        PrimaryOwner = 1,
        InQueue,
        Exists,
        AlreadyOwner,
    }
}
