﻿using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Chat.Data;
using Chat.Data.Entities;
using Chat.Data.Features.Chat;
using Chat.Features.Chat;
using Chat.Observability;
using Chat.Observability.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace Chat.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatDbContext _context;
    private readonly ILogger<ChatController> _logger;
    private readonly Meter _meter;
    private readonly ChatApiOptions _options;
    private readonly HubConnection _hubConnection;
    private readonly Counter<int> _sentMessages;
    private readonly UserActivityTracker _userActivityTracker;
    private readonly HttpClient _imageProcessingClient;

    public ChatController(ChatDbContext context, ILogger<ChatController> logger, Meter meter, ChatApiOptions options, IHttpClientFactory httpClientFactory, HubConnection hubConnection)
    {
        _context = context;
        _logger = logger;
        _meter = meter;
        this._options = options;
        this._hubConnection = hubConnection;
        _sentMessages = _meter.CreateCounter<int>("chatapi.messages_sent", null, "Number of messages sent");
        _userActivityTracker = new UserActivityTracker(meter);
        _imageProcessingClient = httpClientFactory.CreateClient("ImageProcessing");
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ChatMessageResponse>>> GetMessagesSinceDate([FromQuery] DateTime lastMessageDate = default(DateTime))
    {
        try
        {
            var chatMessages = await _context.ChatMessages
                .Include(x => x.ChatMessageImages)
                .Where(x => x.CreatedAt > lastMessageDate)
                .ToListAsync();

            return chatMessages
                    .Where(x => x.CreatedAt.AddTicks(-x.CreatedAt.Ticks % TimeSpan.TicksPerSecond) > lastMessageDate)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => x.ToResponseModel()).ToList();
        }
        catch
        {
            DiagnosticConfig.TrackControllerError(nameof(ChatController), nameof(GetMessagesSinceDate));
            throw;
        }
        finally
        {
            DiagnosticConfig.TrackControllerCall(nameof(ChatController), nameof(GetMessagesSinceDate));
        }
    }

    [HttpPost]
    public async Task<ActionResult<ChatMessage>> PostMessage(NewChatMessageRequest request)
    {
        using (var activity = DiagnosticConfig.ChatApiActivitySource.StartActivity("PostMessage"))
        {
            activity?.AddTag("images", request.Images.Count.ToString());
            try
            {
                var vectorString = JsonSerializer.Serialize(request.VectorClock);
                var dbChatMessage = new ChatMessage()
                {
                    Id = Guid.NewGuid(),
                    MessageText = request.MessageText,
                    UserName = request.UserName,
                    CreatedAt = DateTime.Now,
                    ClientId = request.ClientId,
                    LamportClock = request.LamportTimestamp,
                    VectorClock = vectorString
                };

                await _context.ChatMessages.AddAsync(dbChatMessage);
                await _context.SaveChangesAsync();

                if (request.Images.Count > 0)
                {
                    var imageReferences = request.Images.Select(img => new ChatMessageImage()
                    {
                        Id = Guid.NewGuid(),
                        ChatMessageId = dbChatMessage.Id,
                    });
                    await _context.ChatMessageImages.AddRangeAsync(imageReferences);
                    await _context.SaveChangesAsync();

                    imageReferences = _context.ChatMessageImages.Where(x => x.ChatMessageId == dbChatMessage.Id).ToList();

                    var imageUploadRequests = imageReferences.Zip(request.Images, (image, imgData) =>
                    {
                        return new UploadChatImageRequest()
                        {
                            Id = image.Id,
                            ImageData = imgData
                        };
                    });

                    var response = await _imageProcessingClient.PostAsJsonAsync($"/api/Image/", imageUploadRequests);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception("Failed to upload images");
                    }

                }

                await _context.SaveChangesAsync();

                _logger.LogInformation("Message posted by {UserName} at {CreatedAt}", dbChatMessage.UserName, dbChatMessage.CreatedAt);
                _sentMessages.Add(1);

                _userActivityTracker.TrackUserActivity(dbChatMessage);

                await _hubConnection.SendAsync("NewMessage");

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error posting message");
                DiagnosticConfig.TrackControllerError(nameof(ChatController), nameof(PostMessage));
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
            finally
            {
                DiagnosticConfig.TrackControllerCall(nameof(ChatController), nameof(PostMessage));
            }
        }
    }
}
