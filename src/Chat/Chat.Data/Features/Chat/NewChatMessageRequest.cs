﻿namespace Chat.Data.Features.Chat;

public class NewChatMessageRequest
{
    
    public string UserName { get; set; } = null!;

    public string MessageText { get; set; } = null!;

    public List<string> Images { get; set; } = [];

}
